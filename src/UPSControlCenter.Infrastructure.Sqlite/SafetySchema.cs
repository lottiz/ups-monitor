namespace UPSControlCenter.Infrastructure.Sqlite;

/// <summary>
/// Schema del database locale del Safety Agent. Corrisponde a docs/05-schema-sqlite.md.
/// <para>
/// Applicato in modo idempotente all'avvio: l'agent deve poter partire anche con il database
/// assente, perché è il componente che non può dipendere da nessun altro (§13).
/// </para>
/// </summary>
internal static class SafetySchema
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// <c>synchronous=FULL</c> è deliberato: il caso d'uso è la perdita di alimentazione del
    /// mini PC, quindi ogni commit deve essere su disco. Il volume di scrittura è basso (§5.1).
    /// </summary>
    public const string Pragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = FULL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;
        PRAGMA wal_autocheckpoint = 256;
        """;

    public const string CreateScript = """
        CREATE TABLE IF NOT EXISTS schema_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS safety_config (
            version          INTEGER PRIMARY KEY,
            payload_json     TEXT    NOT NULL,
            checksum_sha256  TEXT    NOT NULL,
            signature        BLOB    NOT NULL,
            received_at      TEXT    NOT NULL,
            activated_at     TEXT    NULL,
            state            TEXT    NOT NULL
                CHECK (state IN ('Received','Active','Superseded','Rejected')),
            rejection_reason TEXT    NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_safety_config_active
            ON safety_config(state) WHERE state = 'Active';

        CREATE TABLE IF NOT EXISTS blackouts (
            blackout_id        TEXT PRIMARY KEY,
            ups_name           TEXT NOT NULL,
            started_at_utc     TEXT NOT NULL,
            started_at_mono_ms INTEGER NOT NULL,
            boot_id            TEXT NOT NULL,
            ended_at_utc       TEXT NULL,
            end_reason         TEXT NULL,
            last_status        TEXT NOT NULL,
            last_runtime_secs  INTEGER NULL,
            last_charge_pct    REAL NULL,
            duration_certain   INTEGER NOT NULL DEFAULT 1
        );

        CREATE INDEX IF NOT EXISTS ix_blackouts_open
            ON blackouts(ended_at_utc) WHERE ended_at_utc IS NULL;

        CREATE TABLE IF NOT EXISTS sequence_runs (
            run_id         TEXT PRIMARY KEY,
            blackout_id    TEXT NOT NULL REFERENCES blackouts(blackout_id),
            sequence_id    TEXT NOT NULL,
            config_version INTEGER NOT NULL,
            fencing_token  INTEGER NOT NULL,
            execution_mode TEXT NOT NULL CHECK (execution_mode IN ('DryRun','Live')),
            owner          TEXT NOT NULL CHECK (owner IN ('Orchestrator','SafetyAgent')),
            state          TEXT NOT NULL,
            started_at     TEXT NOT NULL,
            finished_at    TEXT NULL,
            trigger_reason TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_seq_active
            ON sequence_runs(blackout_id) WHERE state IN ('Pending','Running');

        CREATE TABLE IF NOT EXISTS operations (
            op_id        TEXT PRIMARY KEY,
            run_id       TEXT NOT NULL REFERENCES sequence_runs(run_id),
            step_id      TEXT NOT NULL,
            device_id    TEXT NULL,
            attempt      INTEGER NOT NULL DEFAULT 1,
            action_type  TEXT NOT NULL,
            state        TEXT NOT NULL,
            started_at   TEXT NOT NULL,
            finished_at  TEXT NULL,
            result_code  TEXT NULL,
            message      TEXT NULL,
            was_dry_run  INTEGER NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_logical
            ON operations(run_id, step_id, IFNULL(device_id,''), attempt);

        CREATE TABLE IF NOT EXISTS coordination (
            id                     INTEGER PRIMARY KEY CHECK (id = 1),
            current_owner          TEXT NOT NULL,
            fencing_token          INTEGER NOT NULL DEFAULT 0,
            orchestrator_last_seen TEXT NULL,
            takeover_at            TEXT NULL,
            takeover_reason        TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS critical_events (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at TEXT NOT NULL,
            event_type  TEXT NOT NULL,
            severity    TEXT NOT NULL,
            blackout_id TEXT NULL,
            details     TEXT NULL,
            synced      INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_critical_unsynced
            ON critical_events(synced) WHERE synced = 0;

        CREATE TABLE IF NOT EXISTS recovery_state (
            id                     INTEGER PRIMARY KEY CHECK (id = 1),
            last_clean_shutdown_at TEXT NULL,
            last_boot_id           TEXT NULL,
            last_heartbeat_at      TEXT NULL,
            pending_reconciliation INTEGER NOT NULL DEFAULT 0
        );

        INSERT OR IGNORE INTO coordination (id, current_owner, fencing_token)
            VALUES (1, 'Orchestrator', 0);

        INSERT OR IGNORE INTO recovery_state (id, pending_reconciliation)
            VALUES (1, 0);
        """;
}
