using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UPSControlCenter.Infrastructure.Services;

public sealed class KillSwitchOptions
{
    public const string SectionName = "KillSwitch";

    /// <summary>
    /// File sentinella condiviso con il Safety Agent. È sul filesystem dell'host, non nel
    /// database, perché deve funzionare anche con PostgreSQL e il portale indisponibili (§8.3).
    /// </summary>
    public string SentinelPath { get; set; } = "/var/lib/ups-control-center/AUTOMATION_DISABLED";
}

/// <summary>
/// Kill switch: disabilita immediatamente le automazioni mantenendo attivo il monitoraggio (§15).
/// </summary>
public interface IKillSwitch
{
    bool IsAutomationDisabled();
    string? GetReason();
    Task DisableAsync(string userName, string reason, CancellationToken ct = default);
    Task EnableAsync(string userName, string reason, CancellationToken ct = default);
}

public sealed class FileKillSwitch(
    IOptions<KillSwitchOptions> options,
    ILogger<FileKillSwitch> logger) : IKillSwitch
{
    private readonly string _path = options.Value.SentinelPath;

    /// <summary>
    /// Verifica la presenza del file sentinella. In caso di errore di I/O restituisce
    /// <c>true</c>: se non si può stabilire se le automazioni sono disabilitate, la scelta
    /// sicura è considerarle disabilitate.
    /// </summary>
    public bool IsAutomationDisabled()
    {
        try
        {
            return File.Exists(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex,
                "Impossibile verificare il file sentinella {Path}: le automazioni vengono " +
                "considerate disabilitate per prudenza.", _path);
            return true;
        }
    }

    public string? GetReason()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Motivo non leggibile.";
        }
    }

    public async Task DisableAsync(string userName, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var content = $"disabled_at={DateTimeOffset.UtcNow:O}\nby={userName}\nreason={reason}\n";

        // Scrittura atomica: file temporaneo e spostamento, per non lasciare mai un sentinella
        // parziale che potrebbe essere letto a metà.
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct);
        File.Move(temp, _path, overwrite: true);

        logger.LogWarning(
            "Automazioni DISABILITATE da {User}. Motivo: {Reason}. Il monitoraggio resta attivo.",
            userName, reason);
    }

    public Task EnableAsync(string userName, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        if (File.Exists(_path)) File.Delete(_path);

        logger.LogWarning("Automazioni RIABILITATE da {User}. Motivo: {Reason}.", userName, reason);
        return Task.CompletedTask;
    }
}
