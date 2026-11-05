using UPSControlCenter.Adapters.Live;
using UPSControlCenter.Adapters.Live.Ssh;

namespace UPSControlCenter.Tests.AdaptersLive;

/// <summary>
/// Implementazione programmabile di <see cref="ISshSessionFactory"/>, usata per testare la
/// logica di <see cref="SshDeviceAdapter"/> senza un server SSH reale (non disponibile in
/// questo ambiente di sviluppo, §21).
/// </summary>
internal sealed class FakeSshSessionFactory : ISshSessionFactory
{
    public string FingerprintToPresent { get; set; } = "fake-fingerprint-sha256";
    public List<(string Command, TimeSpan Timeout)> ExecutedCommands { get; } = [];
    public SshCommandResult NextResult { get; set; } = new() { ExitStatus = 0, Output = "ok" };
    public bool ThrowAuthenticationException { get; set; }
    public bool ThrowConnectivityException { get; set; }

    public Task<ISshSession> ConnectAsync(string host, int port, ResolvedCredential credential,
        Func<string, bool> fingerprintPolicy, TimeSpan connectTimeout, CancellationToken ct)
    {
        if (ThrowConnectivityException)
            throw new System.Net.Sockets.SocketException();

        if (!fingerprintPolicy(FingerprintToPresent))
            throw new SshHostKeyRejectedException(FingerprintToPresent);

        if (ThrowAuthenticationException)
            throw new Renci.SshNet.Common.SshAuthenticationException("auth failed");

        return Task.FromResult<ISshSession>(new FakeSshSession(this));
    }

    private sealed class FakeSshSession(FakeSshSessionFactory owner) : ISshSession
    {
        public Task<SshCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            owner.ExecutedCommands.Add((command, timeout));
            return Task.FromResult(owner.NextResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
