using System.Net;
using Microsoft.Extensions.Options;

namespace UPSControlCenter.Web.Security;

public sealed class NetworkRestrictionOptions
{
    public const string SectionName = "NetworkRestriction";

    public bool IsEnabled { get; set; } = true;

    /// <summary>Reti aziendali autorizzate in notazione CIDR (§1.4).</summary>
    public IList<string> AllowedNetworks { get; set; } =
    [
        "10.0.0.0/24",
        "172.16.10.0/24",
        "192.168.98.0/24",
        "192.168.114.0/24",
        // Loopback: necessario al Safety Agent, che chiama il portale su 127.0.0.1 (§1.3).
        "127.0.0.0/8",
        "::1/128"
    ];
}

/// <summary>
/// Filtro IP applicativo.
/// <para>
/// È la <b>terza</b> barriera dopo il binding su IP specifico e le regole <c>DOCKER-USER</c>:
/// Docker scrive direttamente in iptables scavalcando UFW, quindi un controllo a livello
/// applicativo è necessario e non ridondante (§9.4, R9).
/// </para>
/// </summary>
public sealed class NetworkRestrictionMiddleware(
    RequestDelegate next,
    IOptions<NetworkRestrictionOptions> options,
    ILogger<NetworkRestrictionMiddleware> logger)
{
    private readonly List<(IPAddress Network, int PrefixLength)> _allowed =
        [.. ParseNetworks(options.Value.AllowedNetworks, logger)];

    private readonly bool _enabled = options.Value.IsEnabled;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled)
        {
            await next(context);
            return;
        }

        var remote = context.Connection.RemoteIpAddress;

        if (remote is null)
        {
            // Senza indirizzo non si può autorizzare: si nega, non si presume.
            logger.LogWarning("Richiesta senza indirizzo remoto rifiutata ({Path}).",
                context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!IsAllowed(remote))
        {
            logger.LogWarning("Accesso rifiutato da {Ip} verso {Path}: rete non autorizzata.",
                remote, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(
                "Accesso consentito soltanto dalle reti aziendali autorizzate.");
            return;
        }

        await next(context);
    }

    internal bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Un indirizzo IPv4 mappato in IPv6 (::ffff:10.0.0.5) va confrontato nella sua forma
        // IPv4, altrimenti una regola 10.0.0.0/24 non lo riconoscerebbe.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        foreach (var (network, prefix) in _allowed)
        {
            if (network.AddressFamily != address.AddressFamily) continue;
            if (IsInSubnet(address, network, prefix)) return true;
        }

        return false;
    }

    private static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        var addrBytes = address.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (addrBytes.Length != netBytes.Length) return false;

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addrBytes[i] != netBytes[i]) return false;
        }

        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addrBytes[fullBytes] & mask) == (netBytes[fullBytes] & mask);
    }

    private static List<(IPAddress, int)> ParseNetworks(IEnumerable<string> cidrs, ILogger logger)
    {
        var result = new List<(IPAddress, int)>();

        foreach (var cidr in cidrs)
        {
            var parts = cidr.Split('/', 2);
            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out var addr)
                || !int.TryParse(parts[1], out var prefix))
            {
                // Una rete malformata viene scartata con un avviso: includerla significherebbe
                // comportamento indefinito sull'autorizzazione.
                logger.LogError("Rete autorizzata non valida e ignorata: «{Cidr}».", cidr);
                continue;
            }

            var maxPrefix = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            if (prefix < 0 || prefix > maxPrefix)
            {
                logger.LogError("Prefisso non valido per «{Cidr}»: ignorata.", cidr);
                continue;
            }

            result.Add((addr, prefix));
        }

        return result;
    }
}
