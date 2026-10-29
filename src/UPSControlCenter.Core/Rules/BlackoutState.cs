namespace UPSControlCenter.Core.Rules;

/// <summary>
/// Stato di un blackout in corso o concluso. Persistito sia in PostgreSQL sia in SQLite, così
/// che Orchestrator e Safety Agent convergano sullo stesso identificativo (§1.4).
/// </summary>
public sealed record BlackoutState
{
    /// <summary>
    /// Identificativo deterministico: due processi che osservano lo stesso evento fisico
    /// calcolano la stessa stringa, senza bisogno di coordinarsi.
    /// </summary>
    public required string BlackoutId { get; init; }

    public required string UpsName { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Riferimento monotono dell'inizio: valido solo entro lo stesso <see cref="BootId"/>.</summary>
    public required long StartedAtMonotonicMs { get; init; }

    /// <summary>Identificativo dell'avvio del sistema, per capire se il tempo monotono è ancora valido.</summary>
    public required string BootId { get; init; }

    public DateTimeOffset? EndedAtUtc { get; init; }
    public string? EndReason { get; init; }

    /// <summary>
    /// Falso se la durata non è ricostruibile in modo affidabile dopo un riavvio: in tal caso
    /// si applica la policy conservativa configurata e l'incertezza viene registrata (§9.4).
    /// </summary>
    public bool DurationCertain { get; init; } = true;

    public bool IsOpen => EndedAtUtc is null;

    /// <summary>
    /// Durata misurata con il tempo monotono quando possibile. Non usa mai
    /// <c>UtcNow - StartedAtUtc</c> entro lo stesso boot, perché l'orologio può essere corretto
    /// da NTP durante il blackout.
    /// </summary>
    public TimeSpan GetDuration(long nowMonotonicMs, string currentBootId, DateTimeOffset nowUtc)
    {
        if (currentBootId == BootId)
            return TimeSpan.FromMilliseconds(Math.Max(0, nowMonotonicMs - StartedAtMonotonicMs));

        // Boot diverso: il tempo monotono non è confrontabile, resta solo l'orologio di parete.
        var wall = nowUtc - StartedAtUtc;
        return wall < TimeSpan.Zero ? TimeSpan.Zero : wall;
    }

    /// <summary>
    /// Calcola l'identificativo del blackout. L'arrotondamento dell'istante di inizio alla
    /// finestra indicata (default 60s) fa convergere due osservatori che rilevano l'evento con
    /// qualche secondo di differenza.
    /// </summary>
    public static string ComputeId(string upsName, DateTimeOffset startedAtUtc, int windowSeconds = 60)
    {
        var ticksPerWindow = TimeSpan.FromSeconds(windowSeconds).Ticks;
        var bucket = startedAtUtc.UtcTicks / ticksPerWindow;
        return $"{upsName}:{bucket}";
    }
}
