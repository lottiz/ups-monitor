using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Core.Rules;

/// <summary>Risultato dell'elaborazione di una lettura NUT.</summary>
public sealed record TrackerOutcome
{
    public BlackoutState? Blackout { get; init; }

    /// <summary>Eventi da registrare e notificare, generati da questa lettura.</summary>
    public IReadOnlyList<UpsEventType> Events { get; init; } = [];

    /// <summary>Stato accettato dopo il debounce. Può differire dalla lettura appena ricevuta.</summary>
    public UpsStatusFlags DebouncedStatus { get; init; }

    /// <summary>Vero se il blackout si è appena chiuso con questa lettura.</summary>
    public bool BlackoutJustEnded { get; init; }

    /// <summary>Letture OL consecutive: usato per valutare la stabilità prima di riaccendere (§12).</summary>
    public int ConsecutiveOnLineSamples { get; init; }
}

/// <summary>
/// Rileva l'inizio e la fine dei blackout applicando il debounce e il tempo monotono (§9.4).
/// <para>
/// Lo stesso tracker è usato da Orchestrator, Safety Agent e Simulatore: il comportamento
/// simulato e quello reale non possono divergere perché il codice è letteralmente lo stesso.
/// </para>
/// </summary>
public sealed class BlackoutTracker
{
    private readonly IClock _clock;
    private readonly string _bootId;
    private readonly int _debounceSamples;
    private readonly string _upsName;

    private UpsStatusFlags _acceptedStatus = UpsStatusFlags.Unknown;
    private UpsStatusFlags _candidateStatus = UpsStatusFlags.Unknown;
    private int _candidateCount;
    private int _consecutiveOnLine;
    private BlackoutState? _current;

    public BlackoutTracker(string upsName, IClock clock, string bootId, int debounceSamples = 3,
        BlackoutState? restoredState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upsName);
        ArgumentNullException.ThrowIfNull(clock);
        _upsName = upsName;
        _clock = clock;
        _bootId = bootId;
        _debounceSamples = Math.Max(1, debounceSamples);
        _current = restoredState;

        if (restoredState is { IsOpen: true })
            _acceptedStatus = UpsStatusFlags.OnBattery;
    }

    public BlackoutState? Current => _current;
    public UpsStatusFlags AcceptedStatus => _acceptedStatus;

    /// <summary>
    /// Elabora una lettura. Le transizioni ordinarie richiedono <c>debounceSamples</c> letture
    /// concordi; <c>LB</c> è accettato immediatamente perché è una condizione terminale (§7.3).
    /// </summary>
    public TrackerOutcome Process(UpsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var events = new List<UpsEventType>();

        // Comunicazione persa o dato obsoleto: non si assume "online" e non si chiude il blackout.
        if (!snapshot.CommunicationOk || snapshot.IsStale)
        {
            events.Add(snapshot.CommunicationOk ? UpsEventType.StaleData : UpsEventType.CommunicationLost);
            _candidateCount = 0;
            _consecutiveOnLine = 0;
            return new TrackerOutcome
            {
                Blackout = _current,
                Events = events,
                DebouncedStatus = _acceptedStatus,
                ConsecutiveOnLineSamples = 0
            };
        }

        var observed = snapshot.Status;

        // LB bypassa il debounce: è già una condizione terminale dichiarata dall'UPS.
        var accepted = observed.IsLowBattery() ? observed : Debounce(observed);
        if (accepted == UpsStatusFlags.Unknown && _acceptedStatus != UpsStatusFlags.Unknown)
        {
            // Nessuna transizione confermata: si mantiene lo stato precedente.
            return new TrackerOutcome
            {
                Blackout = _current,
                Events = events,
                DebouncedStatus = _acceptedStatus,
                ConsecutiveOnLineSamples = _consecutiveOnLine
            };
        }

        var previous = _acceptedStatus;
        _acceptedStatus = accepted;

        _consecutiveOnLine = accepted.IsOnLine() ? _consecutiveOnLine + 1 : 0;

        var justEnded = false;

        if (accepted.IsOnBattery() && _current is not { IsOpen: true })
        {
            var startedAt = snapshot.AcquiredAt;
            _current = new BlackoutState
            {
                BlackoutId = BlackoutState.ComputeId(_upsName, startedAt),
                UpsName = _upsName,
                StartedAtUtc = startedAt,
                StartedAtMonotonicMs = _clock.MonotonicMilliseconds,
                BootId = _bootId,
                DurationCertain = true
            };
            events.Add(UpsEventType.BlackoutStarted);
        }
        else if (accepted.IsOnLine() && _current is { IsOpen: true })
        {
            _current = _current with
            {
                EndedAtUtc = snapshot.AcquiredAt,
                EndReason = "PowerRestored"
            };
            events.Add(UpsEventType.PowerRestored);
            justEnded = true;
        }

        if (accepted.IsLowBattery() && !previous.IsLowBattery())
            events.Add(UpsEventType.LowBattery);

        if (previous.IsUnknown() && !accepted.IsUnknown() && previous != UpsStatusFlags.None)
            events.Add(UpsEventType.CommunicationRestored);

        return new TrackerOutcome
        {
            Blackout = _current,
            Events = events,
            DebouncedStatus = accepted,
            BlackoutJustEnded = justEnded,
            ConsecutiveOnLineSamples = _consecutiveOnLine
        };
    }

    /// <summary>
    /// Restituisce lo stato osservato solo dopo <c>debounceSamples</c> letture concordi,
    /// altrimenti <see cref="UpsStatusFlags.Unknown"/> per segnalare "nessuna transizione confermata".
    /// </summary>
    private UpsStatusFlags Debounce(UpsStatusFlags observed)
    {
        if (observed == _acceptedStatus)
        {
            _candidateCount = 0;
            return observed;
        }

        if (observed == _candidateStatus)
            _candidateCount++;
        else
        {
            _candidateStatus = observed;
            _candidateCount = 1;
        }

        if (_candidateCount >= _debounceSamples)
        {
            _candidateCount = 0;
            return _candidateStatus;
        }

        // Primo avvio: senza stato precedente non c'è nulla da proteggere, si accetta subito.
        return _acceptedStatus == UpsStatusFlags.Unknown && _candidateCount >= 1
            ? observed
            : UpsStatusFlags.Unknown;
    }

    /// <summary>
    /// Riconcilia lo stato dopo un riavvio. Se il boot è cambiato, il tempo monotono non è più
    /// valido: si tenta la ricostruzione dall'orologio di parete e, se non è plausibile, si marca
    /// la durata come incerta (§5.3).
    /// </summary>
    public static BlackoutState Reconcile(BlackoutState restored, string currentBootId,
        DateTimeOffset nowUtc, TimeSpan maxPlausibleDuration)
    {
        if (restored.BootId == currentBootId)
            return restored;

        var wall = nowUtc - restored.StartedAtUtc;

        // Durata negativa (orologio spostato indietro) o implausibilmente lunga: non affidabile.
        var plausible = wall > TimeSpan.Zero && wall <= maxPlausibleDuration;

        return restored with
        {
            BootId = currentBootId,
            StartedAtMonotonicMs = 0,
            DurationCertain = plausible
        };
    }
}
