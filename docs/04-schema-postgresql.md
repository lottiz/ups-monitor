# 4. Schema database PostgreSQL

Versione: **PostgreSQL 17.6** (tag fissato). Provider: Npgsql + EF Core 10.
Schema applicativo: `ups`. Identity nello schema `identity`.
Tutti i timestamp sono `timestamptz` in UTC. Concorrenza ottimistica tramite `xmin`
(`UseXminAsConcurrencyToken`) sulle entità di configurazione.

## 4.1 Anagrafiche UPS e misure

**`ups.ups_devices`**
| colonna | tipo | note |
|---|---|---|
| id | uuid PK | |
| name | text UQ | |
| description | text NULL | |
| nut_host | text NOT NULL | default `10.0.0.8` |
| nut_port | int NOT NULL | default 3493, CHECK 1..65535 |
| nut_ups_name | text NOT NULL | es. `riello` |
| polling_interval_seconds | int | CHECK 2..300, default 10 |
| communication_timeout_seconds | int | CHECK 5..600, default 30 |
| stale_data_threshold_seconds | int | CHECK 10..3600, default 60 |
| manufacturer / model / serial | text NULL | popolati da NUT, mai inventati |
| is_enabled | bool | |
| created_at / updated_at | timestamptz | |
| xmin | concurrency | |

UQ `(nut_host, nut_port, nut_ups_name)` — impedisce due definizioni dello stesso UPS fisico.

**`ups.ups_measurements`** — serie temporale, append-only
`id bigserial PK`, `ups_device_id uuid FK`, `recorded_at timestamptz`,
`status_raw text` (es. `OB LB`), `status_flags int` (bitmask), `battery_charge_percent numeric(5,2) NULL`,
`battery_runtime_seconds int NULL`, `battery_voltage numeric(8,2) NULL`,
`input_voltage / output_voltage / input_frequency / output_frequency numeric NULL`,
`load_percent numeric(5,2) NULL`, `real_power_watts numeric NULL`,
`output_current_amps / input_current_amps numeric NULL` (solo se il driver li espone),
`phase_data jsonb NULL` (dati trifase quando presenti), `is_stale bool`, `source text`.

> **Tutte le metriche sono nullable per progetto.** `NULL` significa "non fornito da NUT" e
> non viene mai convertito in 0 (§9.3). La bitmask `status_flags` permette query sugli stati
> senza parsing testuale.

Indici: `(ups_device_id, recorded_at DESC)`, BRIN su `recorded_at` per lo storico.
Retention: `SystemSettings.MeasurementRetentionDays` (default 90) + aggregazione oraria in
`ups.ups_measurement_hourly` (min/max/avg) tramite job di manutenzione.

**`ups.ups_events`** — eventi discreti: `event_type` (BlackoutStarted, PowerRestored, LowBattery,
CommunicationLost, StaleData, ThresholdCrossed…), `severity`, `blackout_id text NULL`,
`previous_status / new_status`, `details jsonb`, `occurred_at`, `detected_by` (Orchestrator|SafetyAgent).

## 4.2 Dispositivi gestiti

**`ups.managed_devices`** — tutti i campi §7: `id`, `name` UQ, `description`, `hostname`,
`ip_address inet NULL`, `operating_system`, `device_type` (enum int), `device_group_id FK NULL`,
`priority int` CHECK 1..1000, `shutdown_protocol` / `startup_protocol` (enum int),
`management_port int NULL`, `connection_profile_id FK NULL`, `mac_address macaddr NULL` (WoL),
`shutdown_timeout_seconds` / `startup_timeout_seconds` / `post_action_delay_seconds` int,
`status_check_method` (enum: None, Ping, TcpPort, Ssh, WinRm, Redfish, HttpProbe, Composite),
`status_check_target text NULL`, `is_enabled bool`, `notes text`,
`last_known_state` (enum: Unknown, On, Off, ShuttingDown, Starting, Unreachable, Error),
`last_state_change_at`, `xmin`.

**Alimentazione multi-sorgente** (§6): tabella di giunzione
**`ups.device_power_sources`** `(managed_device_id, ups_device_id, is_primary bool)` PK composta.
Un dispositivo è considerato *senza alimentazione* solo se **tutte** le sue sorgenti sono in blackout.
La vecchia FK diretta `managed_devices.ups_device_id` non esiste: evita l'assunzione 1:1.

**`ups.device_groups`** `(id, name UQ, description, priority, shutdown_order, is_enabled)`.

**`ups.device_dependencies`** `(id, dependent_device_id FK, depends_on_device_id FK, dependency_type
(StorageNfs, StorageSmb, Database, Network, Application, Generic), is_hard bool)`
UQ `(dependent_device_id, depends_on_device_id)`, CHECK `dependent <> depends_on`.
L'aciclicità è verificata in fase applicativa (DAG, §10) perché PostgreSQL non la può esprimere come vincolo.

**`ups.connection_profiles`** `(id, name UQ, auth_type (Password, SshKey, ApiToken, Certificate),
username, secret_ciphertext bytea, secret_key_id text, fingerprint text NULL, is_enabled)`.
I segreti sono cifrati con ASP.NET Core Data Protection (chiavi persistite su volume dedicato,
mai nel repository). **Nessuna colonna in chiaro, nessun segreto nei log.**

## 4.3 Regole e sequenze

**`ups.shutdown_policies`** — contenitore: `name`, `is_active`, `execution_mode` (DryRun|Live),
`max_blackout_duration_seconds` (default 1800), `min_remaining_runtime_seconds` (default 1800),
`debounce_samples int` (default 3), `critical_policy jsonb`, `xmin`.
Indice parziale UQ `(is_active) WHERE is_active` → **una sola policy attiva per volta** (vincolo DB).

**`ups.shutdown_rules`** — `policy_id FK`, `name`, `condition_tree jsonb` (AND/OR annidati),
`priority`, `trigger_sequence_id FK NULL`, `is_enabled`, `time_windows jsonb NULL`,
`days_of_week int NULL` (bitmask).

**`ups.shutdown_sequences`** / **`ups.shutdown_steps`** — step: `sequence_id FK`, `order_index`,
`name`, `step_type` (Notify, ShutdownDevice, ShutdownGroup, Wait, VerifyState, Conditional, Script*),
`target_device_id NULL`, `target_group_id NULL`, `parallel_group int NULL`,
`delay_before_seconds`, `timeout_seconds`, `max_retries` CHECK 0..5, `continue_on_error bool`,
`condition jsonb NULL`, `excluded_device_ids uuid[]`.
UQ `(sequence_id, order_index)`. `Script*` esiste come enum ma **non è implementato**: §15 vieta
comandi shell arbitrari.

**`ups.startup_policies` / `startup_sequences` / `startup_steps`** — simmetrici, con in più
`required_stable_power_seconds` (default 300), `min_battery_charge_percent` (default 50),
`stability_samples int` (default 10).

## 4.4 Esecuzione, audit, sistema

**`ups.orchestration_runs`** — `id`, `blackout_id text NULL`, `run_kind` (Shutdown|Startup|Simulation),
`trigger_reason text`, `execution_mode`, `sequence_id`, `state` (Pending, Running, Completed, Failed,
Aborted, Superseded), `owner` (Orchestrator|SafetyAgent), `fencing_token bigint`,
`started_at`, `finished_at NULL`, `rule_snapshot jsonb`.
UQ parziale: `(blackout_id, run_kind) WHERE state IN ('Pending','Running')` → **impedisce due
sequenze concorrenti per lo stesso blackout** a livello di database.

**`ups.orchestration_actions`** — `run_id FK`, `step_id`, `device_id NULL`, `attempt int`,
`action_type`, `state`, `started_at`, `finished_at`, `result_code`, `message`, `adapter_name`,
`was_dry_run bool`.
UQ `(run_id, step_id, device_id, attempt)` → **idempotenza garantita dal DB** (§13).

**`ups.configuration_versions`** — `id`, `version bigint UQ` (sequenza monotona), `payload jsonb`,
`checksum_sha256 text`, `signature bytea` (HMAC-SHA256 con chiave condivisa dedicata),
`state` (Draft, Validated, Published, Superseded, RolledBack), `created_by`, `created_at`,
`published_at NULL`, `acknowledged_at NULL`, `activated_at NULL`, `activated_by_agent text NULL`,
`validation_errors jsonb NULL`.
Indice parziale UQ `(state) WHERE state = 'Published'` → **una sola versione pubblicata**.

**`ups.worker_heartbeats`** — `worker_name PK`, `instance_id`, `last_seen_at`, `state`,
`fencing_token bigint`, `details jsonb`. Usata per il takeover (§1.4).

**`ups.simulation_runs`** — parametri scenario `jsonb`, timeline `jsonb`, esito, durata, creatore.

**`ups.notification_channels`** `(id, name, channel_type (Smtp|Webhook|Teams), config jsonb,
secret_ciphertext bytea NULL, is_enabled, cooldown_seconds, min_severity)`.
**`ups.notification_events`** `(id, channel_id, event_type, dedup_key, state, attempts, sent_at,
error, payload jsonb)` + UQ parziale su `(dedup_key)` entro finestra di cooldown per la deduplicazione.

**`ups.audit_logs`** — `id`, `occurred_at`, `user_id NULL`, `user_name`, `ip_address inet NULL`,
`action`, `entity_type`, `entity_id`, `before jsonb NULL`, `after jsonb NULL`, `outcome`, `reason text`.
Append-only: nessun UPDATE/DELETE concesso al ruolo applicativo.

**`ups.system_settings`** — chiave/valore tipizzato, con `is_sensitive bool` e audit obbligatorio.
Chiave critica: `ExecutionMode` (default `DryRun`), `LiveEnabledAdapters` (default lista vuota).

## 4.5 Transazioni e consistenza

- Pubblicazione configurazione: singola transazione `SERIALIZABLE` che marca `Superseded` la
  precedente e `Published` la nuova; il vincolo parziale rende impossibile lo stato doppio.
- Avvio run: `INSERT ... ON CONFLICT DO NOTHING` sul vincolo parziale → se restituisce 0 righe,
  un'altra istanza ha già avviato la sequenza e questa rinuncia.
- Registrazione azione: `INSERT` con UQ; `UniqueViolation` è trattata come "già eseguita" (idempotenza).
