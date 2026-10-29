namespace UPSControlCenter.Core.Abstractions;

/// <summary>
/// Astrazione del tempo. Separa deliberatamente orologio di parete e tempo monotono:
/// la durata di un blackout non può essere misurata con <see cref="UtcNow"/>, perché NTP può
/// correggere l'orologio durante il blackout stesso (§9.4).
/// </summary>
public interface IClock
{
    /// <summary>Orologio di parete, per timestamp e persistenza.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Millisecondi da un'origine arbitraria ma monotona crescente. Valido solo entro lo stesso
    /// avvio del sistema: confrontabile con altri valori ottenuti dallo stesso processo/boot.
    /// </summary>
    long MonotonicMilliseconds { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long MonotonicMilliseconds =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);
}
