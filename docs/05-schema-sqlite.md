# 5. Schema SQLite — Safety Agent

Percorso: `/var/lib/ups-control-center/safety.db`
Proprietario: utente `upsagent`, permessi `0750` sulla directory, `0640` sul file.
**Unico scrittore: il Safety Agent.** Nessun container monta questo percorso.

## 5.1 Pragma

```sql
PRAGMA journal_mode = WAL;      -- resistenza ai crash, letture concorrenti
PRAGMA synchronous = FULL;      -- NON NORMAL: durante un blackout il mini PC può
                                -- perdere alimentazione; serve fsync su ogni commit
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
PRAGMA wal_autocheckpoint = 256;
```

`synchronous = FULL` è una scelta deliberata: costa latenza di scrittura ma il caso d'uso è
esattamente "il sistema perde corrente". Il volume di scrittura è basso (decine di righe/minuto).

## 5.2 Tabelle

```sql
-- Configurazione di sicurezza: solo l'ultima valida conta, lo storico serve al rollback
CREATE TABLE safety_config (
    version          INTEGER PRIMARY KEY,         -- monotona, dal Control Plane
    payload_json     TEXT    NOT NULL,
    checksum_sha256  TEXT    NOT NULL,
    signature        BLOB    NOT NULL,
    received_at      TEXT    NOT NULL,            -- ISO-8601 UTC
    activated_at     TEXT    NULL,
    state            TEXT    NOT NULL             -- Received|Active|Superseded|Rejected
        CHECK (state IN ('Received','Active','Superseded','Rejected')),
    rejection_reason TEXT    NULL
);
-- una sola configurazione attiva
CREATE UNIQUE INDEX ux_safety_config_active ON safety_config(state) WHERE state = 'Active';

-- Un record per blackout osservato
CREATE TABLE blackouts (
    blackout_id        TEXT PRIMARY KEY,          -- {upsId}:{ticks} deterministico
    ups_name           TEXT NOT NULL,
    started_at_utc     TEXT NOT NULL,
    started_at_mono_ms INTEGER NOT NULL,          -- Stopwatch monotono, per la durata
    boot_id            TEXT NOT NULL,             -- /proc/sys/kernel/random/boot_id
    ended_at_utc       TEXT NULL,
    end_reason         TEXT NULL,                 -- PowerRestored|CommLost|Shutdown|Unknown
    last_status        TEXT NOT NULL,
    last_runtime_secs  INTEGER NULL,              -- NULL = sconosciuto, mai 0 per default
    last_charge_pct    REAL NULL,
    duration_certain   INTEGER NOT NULL DEFAULT 1 -- 0 dopo un riavvio non ricostruibile
);

CREATE TABLE sequence_runs (
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
-- impedisce due sequenze attive per lo stesso blackout
CREATE UNIQUE INDEX ux_seq_active
    ON sequence_runs(blackout_id) WHERE state IN ('Pending','Running');

CREATE TABLE operations (
    op_id        TEXT PRIMARY KEY,
    run_id       TEXT NOT NULL REFERENCES sequence_runs(run_id),
    step_id      TEXT NOT NULL,
    device_id    TEXT NULL,
    attempt      INTEGER NOT NULL DEFAULT 1,
    action_type  TEXT NOT NULL,
    state        TEXT NOT NULL,                   -- Pending|Running|Succeeded|Failed|Skipped
    started_at   TEXT NOT NULL,
    finished_at  TEXT NULL,
    result_code  TEXT NULL,
    message      TEXT NULL,
    was_dry_run  INTEGER NOT NULL
);
-- IDEMPOTENZA: la chiave logica dell'azione è unica
CREATE UNIQUE INDEX ux_operations_logical
    ON operations(run_id, step_id, IFNULL(device_id,''), attempt);

CREATE TABLE coordination (
    id                      INTEGER PRIMARY KEY CHECK (id = 1),  -- riga singola
    current_owner           TEXT NOT NULL,
    fencing_token           INTEGER NOT NULL DEFAULT 0,
    orchestrator_last_seen  TEXT NULL,
    takeover_at             TEXT NULL,
    takeover_reason         TEXT NULL
);

CREATE TABLE critical_events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at TEXT NOT NULL,
    event_type  TEXT NOT NULL,
    severity    TEXT NOT NULL,
    blackout_id TEXT NULL,
    details     TEXT NULL,
    synced      INTEGER NOT NULL DEFAULT 0   -- 0 = da inviare al Control Plane
);
CREATE INDEX ix_critical_unsynced ON critical_events(synced) WHERE synced = 0;

CREATE TABLE recovery_state (
    id                     INTEGER PRIMARY KEY CHECK (id = 1),
    last_clean_shutdown_at TEXT NULL,
    last_boot_id           TEXT NULL,
    last_heartbeat_at      TEXT NULL,
    pending_reconciliation INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
```

## 5.3 Durata del blackout e tempo monotono

La durata non si calcola mai come `DateTime.UtcNow - started_at_utc` (l'orologio può essere
corretto da NTP durante il blackout). Si usa `Stopwatch.GetTimestamp()` persistito come
`started_at_mono_ms`, valido **solo entro lo stesso `boot_id`**.

Dopo un riavvio del mini PC:

1. Si confronta `boot_id` corrente con `blackouts.boot_id`.
2. Se diverso → il tempo monotono non è più confrontabile. Si tenta la ricostruzione dal
   `started_at_utc` **solo se** lo scostamento con l'orario corrente è plausibile e non ci sono
   segni di correzione d'orologio; in tal caso `duration_certain = 1`.
3. Se la ricostruzione non è affidabile → `duration_certain = 0`, viene registrato un
   `critical_event` di tipo `BlackoutDurationUncertain`, e si applica la **policy conservativa**
   configurata (default: `TreatAsImmediateThreshold` = considera la soglia di durata già raggiunta,
   perché nel dubbio è più sicuro aver già spento che restare acceso con batteria ignota).
   La policy è configurabile ed esplicitamente documentata, come richiesto da §9.4.

## 5.4 Scritture atomiche e backup

- Ogni transizione di stato è una transazione singola; nessuna scrittura parziale osservabile.
- L'applicazione di una nuova configurazione è una transazione che inserisce `Received`,
  valida, poi passa `Active` e marca `Superseded` la precedente: **mai applicazione parziale** (§3.3).
- Backup: **`VACUUM INTO '/var/backups/.../safety-<ts>.db'`** oppure l'API di backup online.
  Mai `cp` del file con WAL attivo (§20).
