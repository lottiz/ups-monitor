using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using UPSControlCenter.Adapters.Live.WakeOnLan;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Verifica <see cref="WakeOnLanStartupAdapter"/> e il formato del magic packet su un vero
/// socket UDP locale: a differenza di SSH/Redfish, Wake-on-LAN è facilmente testabile end-to-end
/// senza un server applicativo, perché è solo un datagramma con un formato fisso (§21).
/// </summary>
public sealed class WakeOnLanTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    public void ParseMacAddress_accetta_i_separatori_comuni(string mac)
    {
        var bytes = WakeOnLanStartupAdapter.ParseMacAddress(mac);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, bytes);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE")]        // troppo corto
    [InlineData("AA:BB:CC:DD:EE:GG")]     // carattere non esadecimale
    [InlineData("non-un-mac")]
    public void ParseMacAddress_rifiuta_i_formati_non_validi(string mac)
    {
        Assert.Throws<FormatException>(() => WakeOnLanStartupAdapter.ParseMacAddress(mac));
    }

    [Fact]
    public void BuildMagicPacket_ha_il_formato_standard_6xFF_piu_16_ripetizioni_del_MAC()
    {
        var mac = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var packet = WakeOnLanStartupAdapter.BuildMagicPacket(mac);

        Assert.Equal(102, packet.Length); // 6 + 16*6
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));

        for (var i = 0; i < 16; i++)
        {
            var segment = packet[(6 + i * 6)..(6 + i * 6 + 6)];
            Assert.Equal(mac, segment);
        }
    }

    [Fact]
    public async Task L_invio_del_magic_packet_raggiunge_davvero_un_socket_UDP_in_ascolto()
    {
        // Un vero datagramma UDP su loopback, non una simulazione: dimostra che il pacchetto
        // costruito dall'adapter è davvero quello che viaggia sulla rete.
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var receiveTask = listener.ReceiveAsync();

        var mac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var packet = WakeOnLanStartupAdapter.BuildMagicPacket(mac);

        using var sender = new UdpClient();
        await sender.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed);

        var received = (await receiveTask).Buffer;
        Assert.Equal(packet, received);
    }

    [Fact]
    public async Task StartupAsync_invoca_il_sender_con_la_porta_9_e_il_pacchetto_corretto()
    {
        var sender = new RecordingSender();
        var adapter = new WakeOnLanStartupAdapter(sender, NullLogger<WakeOnLanStartupAdapter>.Instance);
        var device = new ManagedDevice
        {
            Id = Guid.CreateVersion7(), Name = "pc-desktop", Hostname = "pc-desktop",
            StartupProtocol = StartupProtocol.WakeOnLan, MacAddress = "AA:BB:CC:DD:EE:FF"
        };

        var result = await adapter.StartupAsync(device);

        Assert.True(result.Success);
        Assert.False(result.WasDryRun);
        Assert.Equal(9, sender.LastPort);
        Assert.Equal(WakeOnLanStartupAdapter.BuildMagicPacket([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            sender.LastPacket);
    }

    [Fact]
    public async Task StartupAsync_senza_MAC_si_rifiuta_senza_inviare_nulla()
    {
        var sender = new RecordingSender();
        var adapter = new WakeOnLanStartupAdapter(sender, NullLogger<WakeOnLanStartupAdapter>.Instance);
        var device = new ManagedDevice
        {
            Id = Guid.CreateVersion7(), Name = "senza-mac", Hostname = "x",
            StartupProtocol = StartupProtocol.WakeOnLan
        };

        var result = await adapter.StartupAsync(device);

        Assert.False(result.Success);
        Assert.Equal("WOL_NO_MAC", result.ResultCode);
        Assert.Null(sender.LastPacket);
    }

    [Fact]
    public async Task L_adapter_Wake_on_LAN_non_gestisce_mai_lo_spegnimento()
    {
        var adapter = new WakeOnLanStartupAdapter(new RecordingSender(), NullLogger<WakeOnLanStartupAdapter>.Instance);
        var device = new ManagedDevice { Id = Guid.CreateVersion7(), Name = "x", Hostname = "x" };

        var result = await adapter.ShutdownAsync(device);

        Assert.False(result.Success);
        Assert.Equal("WOL_CANNOT_SHUTDOWN", result.ResultCode);
    }

    private sealed class RecordingSender : IMagicPacketSender
    {
        public byte[]? LastPacket { get; private set; }
        public int? LastPort { get; private set; }

        public Task SendAsync(byte[] packet, int port, CancellationToken ct)
        {
            LastPacket = packet;
            LastPort = port;
            return Task.CompletedTask;
        }
    }
}
