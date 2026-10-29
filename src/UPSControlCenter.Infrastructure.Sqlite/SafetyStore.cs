using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Infrastructure.Sqlite;

public sealed record StoredConfig
{
    public required long Version { get; init; }
    public required string PayloadJson { get; init; }
    public required string ChecksumSha256 { get; init; }
    public required byte[] Signature { get; init; }
    public required string State { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? ActivatedAt { get; init; }
    public string? RejectionReason { get; init; }
}

public sealed record CoordinationState
{
    public required string CurrentOwner { get; init; }
    public required long FencingToken { get; init; }
    public DateTimeOffset? OrchestratorLastSeen { get; init; }
    public DateTimeOffset? TakeoverAt { get; init; }
    public string? TakeoverReason { get; init; }
}

/// <summary>
/// Accesso al database locale del Safety Agent.
/// <para>
/// L'agent è l'unico proprietario di questo file: nessun container lo monta e nessun altro
/// processo vi scrive (§3.2). Tutte le transizioni di stato sono transazioni singole, così non
/// esiste uno stato parzialmente scritto osservabile dopo un crash.
/// </para>
/// </summary>
public sealed class SafetyStore : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SafetyStore> _logger;
    private readonly SqliteConnection _connection;

    public SafetyStore(string databasePath, ILogger<SafetyStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _logger = logger;

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Single writer: l'agent è l'unico scrittore, quindi non serve pooling condiviso.
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        }.ToString();

        // Una connessione persistente: con WAL e un solo scrittore è il modello più semplice e
        // riduce il costo di apertura su ogni ciclo di polling.
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
        ApplyPragmas();
    }

    private void ApplyPragmas()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = SafetySchema.Pragmas;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Crea lo schema se assente. Idempotente: può essere chiamato a ogni avvio.</summary>
    public void EnsureSchema()
    {
        using var tx = _connection.BeginTransaction();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = SafetySchema.CreateScript;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO schema_meta (key, value) VALUES ('version', $v) " +
                "ON CONFLICT(key) DO UPDATE SET value = $v";
            cmd.Parameters.AddWithValue("$v", SafetySchema.CurrentVersion.ToString(CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        _logger.LogInformation("Schema SQLite del Safety Agent verificato (versione {Version}).",
            SafetySchema.CurrentVersion);
    }

    // ---------- configurazione ----------

    public StoredConfig? GetActiveConfig()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT version, payload_json, checksum_sha256, signature, state,
                   received_at, activated_at, rejection_reason
            FROM safety_config WHERE state = 'Active' LIMIT 1
            """;

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadConfig(reader) : null;
    }

    public long GetMaxConfigVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT IFNULL(MAX(version), 0) FROM safety_config";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Attiva una configurazione in una transazione unica: inserimento, attivazione e
    /// superamento della precedente avvengono insieme. Non esiste un istante in cui l'agent
    /// possa osservare due configurazioni attive o nessuna (§3.3).
    /// </summary>
    public void ActivateConfig(long version, string payloadJson, string checksum, byte[] signature,
        DateTimeOffset now)
    {
        using var tx = _connection.BeginTransaction();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE safety_config SET state = 'Superseded' WHERE state = 'Active'";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO safety_config
                    (version, payload_json, checksum_sha256, signature, received_at, activated_at, state)
                VALUES ($v, $p, $c, $s, $r, $a, 'Active')
                ON CONFLICT(version) DO UPDATE SET
                    payload_json = $p, checksum_sha256 = $c, signature = $s,
                    activated_at = $a, state = 'Active', rejection_reason = NULL
                """;
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$p", payloadJson);
            cmd.Parameters.AddWithValue("$c", checksum);
            cmd.Parameters.AddWithValue("$s", signature);
            cmd.Parameters.AddWithValue("$r", Iso(now));
            cmd.Parameters.AddWithValue("$a", Iso(now));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        _logger.LogInformation("Configurazione di sicurezza versione {Version} attivata.", version);
    }

    /// <summary>
    /// Registra una configurazione rifiutata <b>senza toccare quella attiva</b>: una
    /// configurazione non valida non deve mai sostituire una valida (§7.4).
    /// </summary>
    public void RejectConfig(long version, string payloadJson, string checksum, byte[] signature,
        string reason, DateTimeOffset now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO safety_config
                (version, payload_json, checksum_sha256, signature, received_at, state, rejection_reason)
            VALUES ($v, $p, $c, $s, $r, 'Rejected', $reason)
            ON CONFLICT(version) DO UPDATE SET
                state = 'Rejected', rejection_reason = $reason
            """;
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.Parameters.AddWithValue("$c", checksum);
        cmd.Parameters.AddWithValue("$s", signature);
        cmd.Parameters.AddWithValue("$r", Iso(now));
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.ExecuteNonQuery();

        _logger.LogError(
            "Configurazione versione {Version} RIFIUTATA: {Reason}. " +
            "Resta attiva la configurazione precedente.", version, reason);
    }

    // ---------- blackout ----------

    public BlackoutState? GetOpenBlackout()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT blackout_id, ups_name, started_at_utc, started_at_mono_ms, boot_id,
                   ended_at_utc, end_reason, duration_certain
            FROM blackouts WHERE ended_at_utc IS NULL
            ORDER BY started_at_utc DESC LIMIT 1
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new BlackoutState
        {
            BlackoutId = reader.GetString(0),
            UpsName = reader.GetString(1),
            StartedAtUtc = ParseIso(reader.GetString(2)),
            StartedAtMonotonicMs = reader.GetInt64(3),
            BootId = reader.GetString(4),
            EndedAtUtc = reader.IsDBNull(5) ? null : ParseIso(reader.GetString(5)),
            EndReason = reader.IsDBNull(6) ? null : reader.GetString(6),
            DurationCertain = reader.GetInt32(7) != 0
        };
    }

    /// <summary>Inserisce o aggiorna lo stato del blackout. Chiamato a ogni ciclo di polling.</summary>
    public void UpsertBlackout(BlackoutState state, string lastStatus, int? lastRuntime,
        decimal? lastCharge)
    {
        ArgumentNullException.ThrowIfNull(state);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO blackouts
                (blackout_id, ups_name, started_at_utc, started_at_mono_ms, boot_id,
                 ended_at_utc, end_reason, last_status, last_runtime_secs, last_charge_pct,
                 duration_certain)
            VALUES ($id, $ups, $start, $mono, $boot, $end, $reason, $status, $rt, $chg, $certain)
            ON CONFLICT(blackout_id) DO UPDATE SET
                ended_at_utc      = $end,
                end_reason        = $reason,
                last_status       = $status,
                last_runtime_secs = $rt,
                last_charge_pct   = $chg,
                duration_certain  = $certain,
                boot_id           = $boot
            """;
        cmd.Parameters.AddWithValue("$id", state.BlackoutId);
        cmd.Parameters.AddWithValue("$ups", state.UpsName);
        cmd.Parameters.AddWithValue("$start", Iso(state.StartedAtUtc));
        cmd.Parameters.AddWithValue("$mono", state.StartedAtMonotonicMs);
        cmd.Parameters.AddWithValue("$boot", state.BootId);
        cmd.Parameters.AddWithValue("$end", state.EndedAtUtc is { } e ? Iso(e) : DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", state.EndReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$status", lastStatus);
        // NULL resta NULL: autonomia sconosciuta non diventa zero (§9.3).
        cmd.Parameters.AddWithValue("$rt", lastRuntime is { } r ? r : DBNull.Value);
        cmd.Parameters.AddWithValue("$chg", lastCharge is { } c ? (double)c : DBNull.Value);
        cmd.Parameters.AddWithValue("$certain", state.DurationCertain ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    // ---------- sequenze e operazioni ----------

    /// <summary>
    /// Tenta di aprire una sequenza per il blackout indicato.
    /// <para>
    /// Restituisce <c>false</c> se una sequenza è già attiva: la mutua esclusione è imposta
    /// dall'indice univoco parziale, non da un controllo applicativo, quindi regge anche in caso
    /// di esecuzioni concorrenti o riavvii (§5.2, R2).
    /// </para>
    /// </summary>
    public bool TryBeginSequenceRun(string runId, string blackoutId, string sequenceId,
        long configVersion, long fencingToken, ExecutionMode mode, WorkerRole owner,
        string triggerReason, DateTimeOffset now)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sequence_runs
                    (run_id, blackout_id, sequence_id, config_version, fencing_token,
                     execution_mode, owner, state, started_at, trigger_reason)
                VALUES ($run, $bo, $seq, $cfg, $tok, $mode, $owner, 'Running', $now, $reason)
                """;
            cmd.Parameters.AddWithValue("$run", runId);
            cmd.Parameters.AddWithValue("$bo", blackoutId);
            cmd.Parameters.AddWithValue("$seq", sequenceId);
            cmd.Parameters.AddWithValue("$cfg", configVersion);
            cmd.Parameters.AddWithValue("$tok", fencingToken);
            cmd.Parameters.AddWithValue("$mode", mode.ToString());
            cmd.Parameters.AddWithValue("$owner", owner.ToString());
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$reason", triggerReason);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            _logger.LogWarning(
                "Sequenza già attiva per il blackout {BlackoutId}: esecuzione duplicata evitata " +
                "dal vincolo di unicità.", blackoutId);
            return false;
        }
    }

    public void CompleteSequenceRun(string runId, RunState state, DateTimeOffset now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE sequence_runs SET state = $state, finished_at = $now WHERE run_id = $run";
        cmd.Parameters.AddWithValue("$state", state.ToString());
        cmd.Parameters.AddWithValue("$now", Iso(now));
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Registra un'operazione. Restituisce <c>false</c> se l'operazione logica è già presente:
    /// è il meccanismo di idempotenza, basato sul vincolo di unicità (§5.2).
    /// </summary>
    public bool TryRecordOperation(string opId, string runId, string stepId, string? deviceId,
        int attempt, string actionType, ActionState state, DateTimeOffset startedAt,
        DateTimeOffset? finishedAt, string? resultCode, string? message, bool wasDryRun)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO operations
                    (op_id, run_id, step_id, device_id, attempt, action_type, state,
                     started_at, finished_at, result_code, message, was_dry_run)
                VALUES ($op, $run, $step, $dev, $att, $type, $state, $start, $fin, $code, $msg, $dry)
                """;
            cmd.Parameters.AddWithValue("$op", opId);
            cmd.Parameters.AddWithValue("$run", runId);
            cmd.Parameters.AddWithValue("$step", stepId);
            cmd.Parameters.AddWithValue("$dev", deviceId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$att", attempt);
            cmd.Parameters.AddWithValue("$type", actionType);
            cmd.Parameters.AddWithValue("$state", state.ToString());
            cmd.Parameters.AddWithValue("$start", Iso(startedAt));
            cmd.Parameters.AddWithValue("$fin", finishedAt is { } f ? Iso(f) : DBNull.Value);
            cmd.Parameters.AddWithValue("$code", resultCode ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$msg", message ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$dry", wasDryRun ? 1 : 0);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            _logger.LogInformation(
                "Operazione già registrata (run {Run}, step {Step}, dispositivo {Device}, " +
                "tentativo {Attempt}): esecuzione duplicata evitata.",
                runId, stepId, deviceId ?? "-", attempt);
            return false;
        }
    }

    public bool IsOperationCompleted(string runId, string stepId, string? deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM operations
            WHERE run_id = $run AND step_id = $step
              AND IFNULL(device_id,'') = IFNULL($dev,'')
              AND state IN ('Succeeded','Skipped')
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$step", stepId);
        cmd.Parameters.AddWithValue("$dev", deviceId ?? (object)DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    // ---------- coordinamento ----------

    public CoordinationState GetCoordination()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT current_owner, fencing_token, orchestrator_last_seen, takeover_at, takeover_reason
            FROM coordination WHERE id = 1
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new CoordinationState { CurrentOwner = "Orchestrator", FencingToken = 0 };

        return new CoordinationState
        {
            CurrentOwner = reader.GetString(0),
            FencingToken = reader.GetInt64(1),
            OrchestratorLastSeen = reader.IsDBNull(2) ? null : ParseIso(reader.GetString(2)),
            TakeoverAt = reader.IsDBNull(3) ? null : ParseIso(reader.GetString(3)),
            TakeoverReason = reader.IsDBNull(4) ? null : reader.GetString(4)
        };
    }

    /// <summary>
    /// Esegue il takeover incrementando il token di fencing. Il nuovo token invalida le azioni
    /// presentate con token inferiore, quindi un orchestrator che "resuscita" non può riprendere
    /// il comando senza riconciliazione (§1.4).
    /// </summary>
    public long PerformTakeover(string newOwner, string reason, DateTimeOffset now)
    {
        using var tx = _connection.BeginTransaction();

        long token;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE coordination
                SET current_owner = $owner,
                    fencing_token = fencing_token + 1,
                    takeover_at = $now,
                    takeover_reason = $reason
                WHERE id = 1
                RETURNING fencing_token
                """;
            cmd.Parameters.AddWithValue("$owner", newOwner);
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$reason", reason);
            token = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        tx.Commit();

        _logger.LogWarning(
            "Takeover eseguito: nuovo proprietario {Owner}, token di fencing {Token}. Motivo: {Reason}.",
            newOwner, token, reason);

        return token;
    }

    public void RecordOrchestratorHeartbeat(DateTimeOffset seenAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "UPDATE coordination SET orchestrator_last_seen = $now WHERE id = 1";
        cmd.Parameters.AddWithValue("$now", Iso(seenAt));
        cmd.ExecuteNonQuery();
    }

    // ---------- eventi critici ----------

    public void RecordCriticalEvent(UpsEventType type, EventSeverity severity, string? blackoutId,
        string? details, DateTimeOffset now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO critical_events (occurred_at, event_type, severity, blackout_id, details)
            VALUES ($now, $type, $sev, $bo, $det)
            """;
        cmd.Parameters.AddWithValue("$now", Iso(now));
        cmd.Parameters.AddWithValue("$type", type.ToString());
        cmd.Parameters.AddWithValue("$sev", severity.ToString());
        cmd.Parameters.AddWithValue("$bo", blackoutId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$det", details ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public int CountUnsyncedEvents()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM critical_events WHERE synced = 0";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---------- stato di ripristino ----------

    public (string? LastBootId, bool PendingReconciliation) GetRecoveryState()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT last_boot_id, pending_reconciliation FROM recovery_state WHERE id = 1";

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (null, false);

        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1) != 0);
    }

    public void UpdateRecoveryState(string bootId, DateTimeOffset heartbeat, bool pendingReconciliation)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE recovery_state
            SET last_boot_id = $boot, last_heartbeat_at = $hb, pending_reconciliation = $pending
            WHERE id = 1
            """;
        cmd.Parameters.AddWithValue("$boot", bootId);
        cmd.Parameters.AddWithValue("$hb", Iso(heartbeat));
        cmd.Parameters.AddWithValue("$pending", pendingReconciliation ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Backup consistente con <c>VACUUM INTO</c>: copiare il file con WAL attivo produrrebbe un
    /// backup potenzialmente incoerente (§20).
    /// </summary>
    public void BackupTo(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(destinationPath)) File.Delete(destinationPath);

        using var cmd = _connection.CreateCommand();
        // Il percorso non può essere parametrizzato in VACUUM INTO: si raddoppiano gli apici
        // singoli per impedire l'interruzione del letterale.
        cmd.CommandText = $"VACUUM INTO '{destinationPath.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();

        _logger.LogInformation("Backup consistente di SQLite creato in {Path}.", destinationPath);
    }

    private static StoredConfig ReadConfig(SqliteDataReader reader) => new()
    {
        Version = reader.GetInt64(0),
        PayloadJson = reader.GetString(1),
        ChecksumSha256 = reader.GetString(2),
        Signature = (byte[])reader[3],
        State = reader.GetString(4),
        ReceivedAt = ParseIso(reader.GetString(5)),
        ActivatedAt = reader.IsDBNull(6) ? null : ParseIso(reader.GetString(6)),
        RejectionReason = reader.IsDBNull(7) ? null : reader.GetString(7)
    };

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    public void Dispose()
    {
        // Checkpoint prima di chiudere: riduce il WAL e lascia il database in uno stato compatto,
        // utile se il processo viene terminato durante un blackout.
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Checkpoint WAL non completato alla chiusura.");
        }

        _connection.Dispose();
    }
}
