using Renci.SshNet;
using Renci.SshNet.Common;

namespace UPSControlCenter.Adapters.Live.Ssh;

/// <summary>Implementazione di produzione, su SSH.NET.</summary>
public sealed class SshNetSessionFactory : ISshSessionFactory
{
    public async Task<ISshSession> ConnectAsync(string host, int port, ResolvedCredential credential,
        Func<string, bool> fingerprintPolicy, TimeSpan connectTimeout, CancellationToken ct)
    {
        var authMethods = BuildAuthenticationMethods(credential);
        var connectionInfo = new ConnectionInfo(host, port, credential.Username, authMethods)
        {
            Timeout = connectTimeout
        };

        var client = new SshClient(connectionInfo);

        // Nessuna connessione si stabilisce senza una decisione esplicita sul fingerprint:
        // è l'equivalente del rifiuto di StrictHostKeyChecking=no richiesto da §8.4.
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = fingerprintPolicy(e.FingerPrintSHA256);
        };

        try
        {
            await client.ConnectAsync(ct);
        }
        catch (SshConnectionException ex) when (!client.IsConnected)
        {
            client.Dispose();
            throw new SshHostKeyRejectedException(ex.Message);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new SshNetSession(client);
    }

    private static Renci.SshNet.AuthenticationMethod[] BuildAuthenticationMethods(ResolvedCredential credential)
    {
        if (credential.AuthType == Core.Enums.CredentialAuthType.SshKey)
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(credential.Secret));
            // Chiavi protette da passphrase non sono supportate in questa fase: un account di
            // automazione dedicato con chiave senza passphrase, permessi ristretti lato host, è
            // la pratica raccomandata per questo caso d'uso (§8.8), non una riduzione di sicurezza.
            var keyFile = new PrivateKeyFile(keyStream);
            return [new PrivateKeyAuthenticationMethod(credential.Username, keyFile)];
        }

        return [new PasswordAuthenticationMethod(credential.Username, credential.Secret)];
    }

    private sealed class SshNetSession(SshClient client) : ISshSession
    {
        public async Task<SshCommandResult> ExecuteAsync(string command, TimeSpan timeout,
            CancellationToken ct)
        {
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = timeout;

            try
            {
                await cmd.ExecuteAsync(ct);
                return new SshCommandResult
                {
                    ExitStatus = cmd.ExitStatus,
                    Output = cmd.Result,
                    Error = cmd.Error,
                    DisconnectedDuringExecution = false
                };
            }
            catch (SshConnectionException)
            {
                // La connessione è caduta durante l'esecuzione: per un comando di spegnimento è
                // l'esito atteso, non un errore da propagare come tale (§10).
                return new SshCommandResult { DisconnectedDuringExecution = true };
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected) client.Disconnect();
            }
            catch (SshConnectionException)
            {
                // Già caduta: nulla da fare.
            }

            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
