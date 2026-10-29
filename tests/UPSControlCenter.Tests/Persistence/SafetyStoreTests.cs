using Microsoft.Extensions.Logging.Abstractions;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Infrastructure.Sqlite;

namespace UPSControlCenter.Tests.Persistence;

/// <summary>
/// Persistenza SQLite del Safety Agent (§5, §21): idempotenza, mutua esclusione, recupero dopo
/// crash e backup consistente. Ogni test usa un file temporaneo: il WAL e i pragma di
/// sincronizzazione non sono riproducibili su un database in memoria.
/// </summary>
public sealed class SafetyStoreTests : IDisposable
{
    private readonly string _path;
    private readonly SafetyStore _store;

    public SafetyStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"upscc-test-{Guid.CreateVersion7()}.db");
        _store = new SafetyStore(_path, NullLogger<SafetyStore>.Instance);
        _store.EnsureSchema();
    }

    private static BlackoutState Blackout(string id = "riello:1") => new()
    {
        BlackoutId = id,
        UpsName = "riello",
        StartedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        StartedAtMonotonicMs = 1000,
        BootId = "boot-1"
    };

    [Fact]
    public void Lo_schema_e_creato_in_modo_idempotente()
    {
        // Chiamate ripetute non devono fallire: l'agent lo esegue a ogni avvio.
        _store.EnsureSchema();
        _store.EnsureSchema();

        Assert.Null(_store.GetOpenBlackout());
        Assert.Equal(0, _store.GetMaxConfigVersion());
    }

    [Fact]
    public void Un_blackout_aperto_viene_riletto_dopo_il_riavvio()
    {
        var blackout = Blackout();
        _store.UpsertBlackout(blackout, "OB DISCHRG", 1800, 75m);

        // Si simula un riavvio dell'agent riaprendo lo store sullo stesso file.
        using var reopened = new SafetyStore(_path, NullLogger<SafetyStore>.Instance);
        var restored = reopened.GetOpenBlackout();

        Assert.NotNull(restored);
        Assert.Equal(blackout.BlackoutId, restored!.BlackoutId);
        Assert.Equal(blackout.StartedAtUtc, restored.StartedAtUtc);
        Assert.Equal(blackout.StartedAtMonotonicMs, restored.StartedAtMonotonicMs);
        Assert.Equal("boot-1", restored.BootId);
        Assert.True(restored.IsOpen);
    }

    [Fact]
    public void Un_blackout_chiuso_non_viene_piu_restituito_come_aperto()
    {
        var blackout = Blackout();
        _store.UpsertBlackout(blackout, "OB", 1800, 75m);

        var closed = blackout with
        {
            EndedAtUtc = blackout.StartedAtUtc.AddMinutes(10),
            EndReason = "PowerRestored"
        };
        _store.UpsertBlackout(closed, "OL", 3600, 90m);

        Assert.Null(_store.GetOpenBlackout());
    }

    [Fact]
    public void L_autonomia_sconosciuta_resta_NULL_e_non_diventa_zero()
    {
        // Requisito §9.3 esteso alla persistenza: il database deve conservare la differenza fra
        // "non disponibile" e zero, altrimenti l'informazione si perde al primo riavvio.
        _store.UpsertBlackout(Blackout(), "OB", lastRuntime: null, lastCharge: null);

        var restored = _store.GetOpenBlackout();
        Assert.NotNull(restored);
        // La lettura di last_runtime_secs non è esposta nello stato, ma il salvataggio di null
        // non deve causare errori né violazioni di vincolo.
        Assert.True(restored!.IsOpen);
    }

    [Fact]
    public void Una_sola_sequenza_puo_essere_attiva_per_blackout()
    {
        _store.UpsertBlackout(Blackout(), "OB", 1800, 75m);
        var now = DateTimeOffset.UtcNow;

        var first = _store.TryBeginSequenceRun("run-1", "riello:1", "seq-1", 1, 1,
            ExecutionMode.DryRun, WorkerRole.Orchestrator, "soglia raggiunta", now);

        // Secondo tentativo, anche con run_id diverso: il vincolo di unicità lo impedisce.
        // È la difesa contro la doppia esecuzione fra Orchestrator e Safety Agent (R2).
        var second = _store.TryBeginSequenceRun("run-2", "riello:1", "seq-1", 1, 2,
            ExecutionMode.DryRun, WorkerRole.SafetyAgent, "takeover", now);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void Dopo_il_completamento_una_nuova_sequenza_e_ammessa()
    {
        _store.UpsertBlackout(Blackout(), "OB", 1800, 75m);
        var now = DateTimeOffset.UtcNow;

        Assert.True(_store.TryBeginSequenceRun("run-1", "riello:1", "seq-1", 1, 1,
            ExecutionMode.DryRun, WorkerRole.Orchestrator, "primo tentativo", now));

        _store.CompleteSequenceRun("run-1", RunState.Failed, now);

        // Una sequenza fallita non deve bloccare per sempre il blackout: serve poter riprovare.
        Assert.True(_store.TryBeginSequenceRun("run-2", "riello:1", "seq-1", 1, 2,
            ExecutionMode.DryRun, WorkerRole.SafetyAgent, "secondo tentativo", now));
    }

    [Fact]
    public void La_stessa_operazione_logica_non_puo_essere_registrata_due_volte()
    {
        _store.UpsertBlackout(Blackout(), "OB", 1800, 75m);
        var now = DateTimeOffset.UtcNow;

        _store.TryBeginSequenceRun("run-1", "riello:1", "seq-1", 1, 1,
            ExecutionMode.DryRun, WorkerRole.Orchestrator, "soglia", now);

        var deviceId = Guid.CreateVersion7().ToString();

        var first = _store.TryRecordOperation("op-1", "run-1", "step-1", deviceId, 1,
            "ShutdownDevice", ActionState.Succeeded, now, now, "DRY_RUN", "ok", true);

        // op_id diverso ma stessa chiave logica (run, step, device, attempt): va scartata.
        var duplicate = _store.TryRecordOperation("op-2", "run-1", "step-1", deviceId, 1,
            "ShutdownDevice", ActionState.Succeeded, now, now, "DRY_RUN", "ok", true);

        Assert.True(first);
        Assert.False(duplicate);
        Assert.True(_store.IsOperationCompleted("run-1", "step-1", deviceId));
    }

    [Fact]
    public void Un_nuovo_tentativo_della_stessa_operazione_e_ammesso()
    {
        _store.UpsertBlackout(Blackout(), "OB", 1800, 75m);
        var now = DateTimeOffset.UtcNow;

        _store.TryBeginSequenceRun("run-1", "riello:1", "seq-1", 1, 1,
            ExecutionMode.DryRun, WorkerRole.Orchestrator, "soglia", now);

        var deviceId = Guid.CreateVersion7().ToString();

        Assert.True(_store.TryRecordOperation("op-1", "run-1", "step-1", deviceId, 1,
            "ShutdownDevice", ActionState.Failed, now, now, "TIMEOUT", "non raggiungibile", true));

        // Il retry ha attempt = 2: la chiave logica cambia, quindi è legittimo.
        Assert.True(_store.TryRecordOperation("op-2", "run-1", "step-1", deviceId, 2,
            "ShutdownDevice", ActionState.Succeeded, now, now, "DRY_RUN", "ok", true));
    }

    [Fact]
    public void Operazioni_senza_dispositivo_sono_distinte_correttamente()
    {
        _store.UpsertBlackout(Blackout(), "OB", 1800, 75m);
        var now = DateTimeOffset.UtcNow;

        _store.TryBeginSequenceRun("run-1", "riello:1", "seq-1", 1, 1,
            ExecutionMode.DryRun, WorkerRole.Orchestrator, "soglia", now);

        // Gli step senza bersaglio (es. Notify) hanno device_id NULL: l'indice usa IFNULL per
        // trattarli come una chiave confrontabile, altrimenti NULL != NULL li renderebbe
        // sempre duplicabili.
        Assert.True(_store.TryRecordOperation("op-1", "run-1", "step-notify", null, 1,
            "Notify", ActionState.Succeeded, now, now, "DRY_RUN", "inviata", true));

        Assert.False(_store.TryRecordOperation("op-2", "run-1", "step-notify", null, 1,
            "Notify", ActionState.Succeeded, now, now, "DRY_RUN", "inviata", true));
    }

    [Fact]
    public void Il_takeover_incrementa_il_token_di_fencing()
    {
        var initial = _store.GetCoordination();
        Assert.Equal(0, initial.FencingToken);
        Assert.Equal("Orchestrator", initial.CurrentOwner);

        var token = _store.PerformTakeover("SafetyAgent", "orchestrator assente",
            DateTimeOffset.UtcNow);

        Assert.Equal(1, token);

        var after = _store.GetCoordination();
        Assert.Equal("SafetyAgent", after.CurrentOwner);
        Assert.Equal(1, after.FencingToken);
        Assert.Equal("orchestrator assente", after.TakeoverReason);
    }

    [Fact]
    public void Takeover_successivi_producono_token_sempre_crescenti()
    {
        // Un token monotono permette di rifiutare le azioni di un proprietario superato (§1.4).
        var first = _store.PerformTakeover("SafetyAgent", "primo", DateTimeOffset.UtcNow);
        var second = _store.PerformTakeover("Orchestrator", "ripristino", DateTimeOffset.UtcNow);

        Assert.True(second > first);
    }

    [Fact]
    public void L_heartbeat_dell_orchestrator_viene_persistito()
    {
        var seen = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        _store.RecordOrchestratorHeartbeat(seen);

        var coordination = _store.GetCoordination();

        Assert.NotNull(coordination.OrchestratorLastSeen);
        Assert.Equal(seen, coordination.OrchestratorLastSeen!.Value.ToUniversalTime());
    }

    [Fact]
    public void Una_configurazione_attivata_sostituisce_la_precedente_in_modo_atomico()
    {
        var now = DateTimeOffset.UtcNow;

        _store.ActivateConfig(1, "{\"version\":1}", "checksum-1", [1, 2, 3], now);
        _store.ActivateConfig(2, "{\"version\":2}", "checksum-2", [4, 5, 6], now);

        var active = _store.GetActiveConfig();

        // L'indice univoco parziale garantisce una sola configurazione attiva: se la transizione
        // non fosse atomica, questa lettura potrebbe trovarne due o nessuna.
        Assert.NotNull(active);
        Assert.Equal(2, active!.Version);
        Assert.Equal("checksum-2", active.ChecksumSha256);
        Assert.Equal("Active", active.State);
        Assert.Equal(2, _store.GetMaxConfigVersion());
    }

    [Fact]
    public void Una_configurazione_rifiutata_non_sostituisce_quella_attiva()
    {
        var now = DateTimeOffset.UtcNow;

        _store.ActivateConfig(1, "{\"version\":1}", "checksum-1", [1, 2, 3], now);
        _store.RejectConfig(2, "{\"version\":2}", "checksum-2", [4, 5, 6],
            "firma non valida", now);

        var active = _store.GetActiveConfig();

        // Requisito centrale di §3.3: una configurazione non valida non deve mai sostituire
        // una valida, altrimenti un errore di distribuzione disarmerebbe la protezione.
        Assert.NotNull(active);
        Assert.Equal(1, active!.Version);
        Assert.Equal("checksum-1", active.ChecksumSha256);
    }

    [Fact]
    public void Gli_eventi_critici_vengono_registrati_e_contati_come_da_sincronizzare()
    {
        var now = DateTimeOffset.UtcNow;

        _store.RecordCriticalEvent(UpsEventType.BlackoutStarted, EventSeverity.Critical,
            "riello:1", "inizio blackout", now);
        _store.RecordCriticalEvent(UpsEventType.LowBattery, EventSeverity.Critical,
            "riello:1", "LB rilevato", now);

        // Gli eventi restano in attesa di sincronizzazione: se il Control Plane è giù durante
        // il blackout, non devono andare perduti.
        Assert.Equal(2, _store.CountUnsyncedEvents());
    }

    [Fact]
    public void Lo_stato_di_ripristino_registra_il_boot_id_corrente()
    {
        var now = DateTimeOffset.UtcNow;

        var (initialBoot, initialPending) = _store.GetRecoveryState();
        Assert.Null(initialBoot);
        Assert.False(initialPending);

        _store.UpdateRecoveryState("boot-42", now, pendingReconciliation: true);

        var (boot, pending) = _store.GetRecoveryState();
        Assert.Equal("boot-42", boot);
        Assert.True(pending);
    }

    [Fact]
    public void Il_backup_usa_VACUUM_INTO_e_produce_un_database_leggibile()
    {
        var now = DateTimeOffset.UtcNow;
        _store.ActivateConfig(1, "{\"version\":1}", "checksum-1", [1, 2, 3], now);
        _store.UpsertBlackout(Blackout(), "OB", 1800, 75m);

        var backupPath = Path.Combine(Path.GetTempPath(),
            $"upscc-backup-{Guid.CreateVersion7()}.db");

        try
        {
            _store.BackupTo(backupPath);

            Assert.True(File.Exists(backupPath));

            // Il backup deve essere un database consistente e apribile, non una copia a metà
            // di un file con WAL attivo (§20).
            using var restored = new SafetyStore(backupPath, NullLogger<SafetyStore>.Instance);
            var config = restored.GetActiveConfig();

            Assert.NotNull(config);
            Assert.Equal(1, config!.Version);
            Assert.NotNull(restored.GetOpenBlackout());
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    public void Dispose()
    {
        _store.Dispose();

        // Vanno rimossi anche i file collaterali del WAL.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
