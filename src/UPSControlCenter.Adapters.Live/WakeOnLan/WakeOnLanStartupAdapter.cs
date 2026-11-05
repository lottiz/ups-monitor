using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Adapters.Live.WakeOnLan;

/// <summary>
/// Adapter di accensione tramite Wake-on-LAN (§12).
/// <para>
/// Invia il "magic packet" via UDP broadcast: un datagramma fire-and-forget, senza conferma di
/// ricezione. Per questo <see cref="StartupAsync"/> non può affermare che il dispositivo si sia
/// davvero acceso — solo che il pacchetto è stato inviato — e <see cref="CheckStateAsync"/> non
/// è implementabile tramite questo stesso meccanismo: la verifica dello stato dopo un
/// Wake-on-LAN spetta al metodo configurato in <see cref="ManagedDevice.StatusCheckMethod"/>
/// (tipicamente <c>Ping</c> o <c>TcpPort</c>), non a questo adapter.
/// </para>
/// <para>
/// <b>Non gestisce mai lo spegnimento</b>: non esiste un valore Wake-on-LAN in
/// <see cref="ShutdownProtocol"/>, e <see cref="ShutdownAsync"/> lo riflette esplicitamente.
/// </para>
/// <para>
/// La capability Wake-on-LAN del dispositivo non è presunta funzionante: molte schede di rete
/// la perdono dopo una perdita completa di alimentazione se non configurata nel BIOS/UEFI (R19).
/// Questo adapter invia il pacchetto comunque se richiesto; è compito dell'operatore aver
/// verificato la capability con <see cref="TestConnectionAsync"/> prima di affidarvisi in una
/// sequenza reale.
/// </para>
/// </summary>
public sealed class WakeOnLanStartupAdapter(
    IMagicPacketSender packetSender,
    ILogger<WakeOnLanStartupAdapter> logger) : IDeviceAdapter
{
    public const string Name = "WakeOnLan";
    private const int WakeOnLanPort = 9;

    public string AdapterName => Name;
    public bool PerformsRealOperations => true;
    public StartupProtocol? SupportedStartupProtocol => StartupProtocol.WakeOnLan;

    public bool CanHandle(ManagedDevice device) => device.StartupProtocol == StartupProtocol.WakeOnLan;

    public Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default) =>
        Task.FromResult(AdapterResult.Fail("WOL_CANNOT_SHUTDOWN", dryRun: false,
            "Wake-on-LAN non può spegnere un dispositivo: non esiste un comando di spegnimento " +
            "in questo protocollo."));

    public async Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(device.MacAddress))
        {
            return AdapterResult.Fail("WOL_NO_MAC", dryRun: false,
                "Nessun indirizzo MAC configurato: impossibile costruire il magic packet.");
        }

        byte[] macBytes;
        try
        {
            macBytes = ParseMacAddress(device.MacAddress);
        }
        catch (FormatException ex)
        {
            return AdapterResult.Fail("WOL_INVALID_MAC", dryRun: false,
                $"Indirizzo MAC non valido: {ex.Message}");
        }

        try
        {
            await packetSender.SendAsync(BuildMagicPacket(macBytes), WakeOnLanPort, ct);

            logger.LogInformation(
                "Magic packet Wake-on-LAN inviato per «{Device}» (MAC {Mac}). Nessuna conferma " +
                "di ricezione è possibile: la verifica dello stato dipende dal metodo configurato.",
                device.Name, device.MacAddress);

            return AdapterResult.Ok("WOL_PACKET_SENT", dryRun: false,
                "Magic packet inviato. L'accensione effettiva non è confermata da questo " +
                "protocollo: verificare lo stato con il metodo configurato.", DeviceState.Starting);
        }
        catch (SocketException ex)
        {
            return AdapterResult.Fail("WOL_SEND_FAILED", dryRun: false,
                $"Invio del magic packet non riuscito: {ex.Message}");
        }
    }

    /// <summary>
    /// Wake-on-LAN non ha un canale di verifica proprio: la responsabilità è del metodo
    /// configurato su <see cref="ManagedDevice.StatusCheckMethod"/>, gestito altrove (non da
    /// questo adapter, che si occupa solo dell'invio del pacchetto).
    /// </summary>
    public Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default) =>
        Task.FromResult(AdapterResult.Ok("WOL_NO_STATE_CHECK", dryRun: false,
            "Wake-on-LAN non fornisce un canale di verifica dello stato: configurare un metodo " +
            "di verifica (Ping, TcpPort) sul dispositivo.", DeviceState.Unknown));

    /// <summary>
    /// Non distruttivo per definizione (un magic packet non ha effetti su un dispositivo già
    /// acceso), ma verifica solo che l'indirizzo MAC sia valido e il pacchetto inviabile: non
    /// può confermare che il dispositivo lo riceva o reagisca.
    /// </summary>
    public Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(device.MacAddress))
        {
            return Task.FromResult(AdapterResult.Fail("WOL_NO_MAC", dryRun: false,
                "Nessun indirizzo MAC configurato."));
        }

        try
        {
            ParseMacAddress(device.MacAddress);
        }
        catch (FormatException ex)
        {
            return Task.FromResult(AdapterResult.Fail("WOL_INVALID_MAC", dryRun: false,
                $"Indirizzo MAC non valido: {ex.Message}"));
        }

        return Task.FromResult(AdapterResult.Ok("WOL_MAC_VALID", dryRun: false,
            "Indirizzo MAC valido. Il test non invia un pacchetto reale: usare «Accendi» per " +
            "verificare davvero la capability Wake-on-LAN del dispositivo (R19, non presunta)."));
    }

    internal static byte[] ParseMacAddress(string mac)
    {
        var parts = mac.Split([':', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6)
            throw new FormatException($"Formato MAC non valido: «{mac}» (attesi 6 gruppi esadecimali).");

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                throw new FormatException($"Gruppo non esadecimale in «{mac}»: «{parts[i]}».");
        }

        return bytes;
    }

    /// <summary>Costruisce il magic packet: 6 byte 0xFF seguiti da 16 ripetizioni del MAC.</summary>
    internal static byte[] BuildMagicPacket(byte[] macBytes)
    {
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);
        return packet;
    }
}

/// <summary>
/// Invia il magic packet. Astratto per permettere i test senza una rete reale e senza i
/// permessi che il broadcast UDP può richiedere in alcuni ambienti containerizzati.
/// </summary>
public interface IMagicPacketSender
{
    Task SendAsync(byte[] packet, int port, CancellationToken ct);
}

public sealed class UdpBroadcastMagicPacketSender : IMagicPacketSender
{
    public async Task SendAsync(byte[] packet, int port, CancellationToken ct)
    {
        using var client = new UdpClient { EnableBroadcast = true };
        await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, port));
    }
}
