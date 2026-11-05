using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Adapters.Live.Ssh;

/// <summary>
/// Configura <see cref="SshDeviceAdapter"/> per un protocollo specifico. Linux generico e QNAP
/// condividono lo stesso meccanismo di trasporto (SSH) ma restano due adapter distinti, con
/// nomi distinti ai fini di §8.2: l'abilitazione LIVE è per singolo adapter, e approvare SSH per
/// i server Linux non deve implicitamente approvare anche il NAS.
/// </summary>
public sealed record SshShutdownProfile
{
    public required string AdapterName { get; init; }
    public required ShutdownProtocol Protocol { get; init; }
    public required string ShutdownCommand { get; init; }

    /// <summary>
    /// Server Linux generici: l'utente di automazione ha un diritto sudo limitato al solo
    /// comando di spegnimento, configurato in <c>/etc/sudoers.d/</c> sull'host (§8.8). L'adapter
    /// non può verificare che quella restrizione esista: la documenta come prerequisito.
    /// </summary>
    public static SshShutdownProfile Linux { get; } = new()
    {
        AdapterName = "Ssh",
        Protocol = ShutdownProtocol.Ssh,
        ShutdownCommand = "sudo /sbin/shutdown -h now"
    };

    /// <summary>QNAP QTS: <c>poweroff</c> è il comando documentato via SSH, senza richiedere sudo.</summary>
    public static SshShutdownProfile Qnap { get; } = new()
    {
        AdapterName = "QnapSsh",
        Protocol = ShutdownProtocol.QnapSsh,
        ShutdownCommand = "poweroff"
    };
}
