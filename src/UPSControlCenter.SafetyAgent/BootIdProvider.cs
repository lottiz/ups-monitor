namespace UPSControlCenter.SafetyAgent;

/// <summary>
/// Identificativo dell'avvio corrente del sistema. Serve a stabilire se un riferimento di tempo
/// monotono persistito è ancora confrontabile (§5.3).
/// </summary>
public static class BootIdProvider
{
    private const string LinuxBootIdPath = "/proc/sys/kernel/random/boot_id";

    /// <summary>
    /// Legge <c>/proc/sys/kernel/random/boot_id</c>, che cambia a ogni riavvio del kernel.
    /// <para>
    /// Dove non disponibile (ambienti non-Linux o filesystem limitato) ricade su un valore
    /// derivato dall'istante di avvio del processo. È meno preciso — un riavvio del solo processo
    /// risulterebbe come un nuovo boot — ma l'errore è nella direzione sicura: porta a
    /// considerare incerta una durata che potrebbe essere valida, non il contrario.
    /// </para>
    /// </summary>
    public static string Get()
    {
        try
        {
            if (File.Exists(LinuxBootIdPath))
            {
                var value = File.ReadAllText(LinuxBootIdPath).Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si prosegue con il fallback.
        }

        var startUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"fallback-{startUtc:yyyyMMddHHmmss}";
    }
}
