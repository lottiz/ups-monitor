using UPSControlCenter.Adapters.Live;
using UPSControlCenter.Adapters.Live.WinRm;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Implementazione programmabile di <see cref="IWinRmSessionFactory"/>, usata per testare la
/// logica di <see cref="WinRmShutdownAdapter"/> senza un host Windows reale (non disponibile in
/// questo ambiente di sviluppo, §21, stesso approccio di <see cref="FakeSshSessionFactory"/>).
/// </summary>
internal sealed class FakeWinRmSessionFactory : IWinRmSessionFactory
{
    public string ThumbprintToPresent { get; set; } = "fake-thumbprint-sha256";
    public List<(string Command, TimeSpan Timeout)> ExecutedCommands { get; } = [];
    public WinRmCommandResult NextResult { get; set; } = new(0, "ok", "");
    public bool ThrowAuthenticationException { get; set; }
    public bool ThrowConnectivityException { get; set; }

    public Task<IWinRmSession> ConnectAsync(string host, int port, ResolvedCredential credential,
        Func<string, bool> certificateThumbprintPolicy, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        if (ThrowConnectivityException)
            throw new System.Net.Sockets.SocketException();

        if (!certificateThumbprintPolicy(ThumbprintToPresent))
            throw new WinRmCertificateRejectedException("certificato non corrispondente");

        if (ThrowAuthenticationException)
            throw new WinRmAuthenticationException("auth failed");

        return Task.FromResult<IWinRmSession>(new FakeWinRmSession(this));
    }

    private sealed class FakeWinRmSession(FakeWinRmSessionFactory owner) : IWinRmSession
    {
        public Task<WinRmCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct = default)
        {
            owner.ExecutedCommands.Add((command, timeout));
            return Task.FromResult(owner.NextResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
