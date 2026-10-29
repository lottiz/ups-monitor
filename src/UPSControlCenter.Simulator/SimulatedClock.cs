using UPSControlCenter.Core.Abstractions;

namespace UPSControlCenter.Simulator;

/// <summary>
/// Orologio controllato dal simulatore. Permette di comprimere mezz'ora di blackout in
/// millisecondi di test, mantenendo la coerenza fra tempo di parete e tempo monotono.
/// </summary>
public sealed class SimulatedClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;
    private long _monotonic;

    public DateTimeOffset UtcNow => _now;
    public long MonotonicMilliseconds => _monotonic;

    /// <summary>Avanza entrambi gli orologi in modo coerente.</summary>
    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
        _monotonic += (long)delta.TotalMilliseconds;
    }

    /// <summary>
    /// Sposta solo l'orologio di parete, lasciando fermo quello monotono: riproduce una
    /// correzione NTP durante un blackout, scenario che le regole devono tollerare (§9.4).
    /// </summary>
    public void SkewWallClock(TimeSpan delta) => _now = _now.Add(delta);
}
