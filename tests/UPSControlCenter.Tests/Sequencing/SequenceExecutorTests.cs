using UPSControlCenter.Core.Abstractions;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Rules;
using UPSControlCenter.Core.Sequencing;

namespace UPSControlCenter.Tests.Sequencing;

/// <summary>
/// Adapter di test: configurabile per fallire, essere lento o restituire stati di verifica
/// specifici. Non esegue mai operazioni reali, come ogni adapter usato nei test (§21).
/// </summary>
internal sealed class ScriptedAdapter : IDeviceAdapter
{
    public string AdapterName => "Scripted";
    public bool PerformsRealOperations => false;
    public bool CanHandle(ManagedDevice device) => true;

    public HashSet<Guid> FailFirstAttemptFor { get; } = [];
    public HashSet<Guid> AlwaysFailFor { get; } = [];
    public Dictionary<Guid, TimeSpan> DelayFor { get; } = [];
    public Dictionary<Guid, DeviceState> StateAfterShutdown { get; } = [];
    public Dictionary<Guid, DeviceState> StateAfterStartup { get; } = [];

    public List<(Guid DeviceId, string Action)> Calls { get; } = [];

    private readonly Dictionary<Guid, int> _attempts = [];

    public async Task<AdapterResult> ShutdownAsync(ManagedDevice device, CancellationToken ct = default)
    {
        Calls.Add((device.Id, "Shutdown"));
        if (DelayFor.TryGetValue(device.Id, out var d)) await Task.Delay(d, ct);

        if (AlwaysFailFor.Contains(device.Id))
            return AdapterResult.Fail("SCRIPTED_FAIL", true, "Fallimento programmato.");

        if (FailFirstAttemptFor.Contains(device.Id))
        {
            _attempts.TryGetValue(device.Id, out var count);
            _attempts[device.Id] = count + 1;
            if (count == 0)
                return AdapterResult.Fail("SCRIPTED_FAIL_ONCE", true, "Primo tentativo programmato per fallire.");
        }

        return AdapterResult.Ok("SHUTDOWN_OK", true, "Spegnimento simulato.", DeviceState.ShuttingDown);
    }

    public async Task<AdapterResult> StartupAsync(ManagedDevice device, CancellationToken ct = default)
    {
        Calls.Add((device.Id, "Startup"));
        if (DelayFor.TryGetValue(device.Id, out var d)) await Task.Delay(d, ct);

        if (AlwaysFailFor.Contains(device.Id))
            return AdapterResult.Fail("SCRIPTED_FAIL", true, "Fallimento programmato.");

        return AdapterResult.Ok("STARTUP_OK", true, "Accensione simulata.", DeviceState.Starting);
    }

    public Task<AdapterResult> CheckStateAsync(ManagedDevice device, CancellationToken ct = default)
    {
        Calls.Add((device.Id, "Check"));

        if (StateAfterShutdown.TryGetValue(device.Id, out var afterShutdown))
            return Task.FromResult(AdapterResult.Ok("CHECK", true, null, afterShutdown));

        if (StateAfterStartup.TryGetValue(device.Id, out var afterStartup))
            return Task.FromResult(AdapterResult.Ok("CHECK", true, null, afterStartup));

        return Task.FromResult(AdapterResult.Ok("CHECK", true, null, DeviceState.Unknown));
    }

    public Task<AdapterResult> TestConnectionAsync(ManagedDevice device, CancellationToken ct = default) =>
        Task.FromResult(AdapterResult.Ok("TEST", true));
}

internal sealed class SingleAdapterSelector(IDeviceAdapter adapter) : IAdapterSelector
{
    public ExecutionMode EffectiveMode => ExecutionMode.DryRun;
    public IDeviceAdapter Select(ManagedDevice device) => adapter;
}

public sealed class SequenceExecutorTests
{
    private static ExecutableStep Step(string name, int order, SequenceStepType type,
        params Guid[] targets) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderIndex = order,
        Name = name,
        StepType = type,
        TargetDeviceIds = targets,
        TimeoutSeconds = 5
    };

    private static ManagedDevice Device(string name) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = name,
        Hostname = $"{name}.local",
        ShutdownProtocol = ShutdownProtocol.Ssh,
        StartupProtocol = StartupProtocol.WakeOnLan
    };

    [Fact]
    public async Task Gli_step_vengono_eseguiti_nell_ordine_configurato()
    {
        var adapter = new ScriptedAdapter();
        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var app = Device("app");
        var db = Device("db");

        var steps = new[]
        {
            Step("Spegni app", 10, SequenceStepType.ShutdownDevice, app.Id),
            Step("Spegni db", 20, SequenceStepType.ShutdownDevice, db.Id)
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [app.Id] = app, [db.Id] = db };

        var result = await executor.ExecuteAsync("run-1", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Completed, result.FinalState);
        // L'ordine delle chiamate deve rispettare OrderIndex: prima l'app, poi il database.
        var shutdownCalls = adapter.Calls.Where(c => c.Action == "Shutdown").ToList();
        Assert.Equal(app.Id, shutdownCalls[0].DeviceId);
        Assert.Equal(db.Id, shutdownCalls[1].DeviceId);
    }

    [Fact]
    public async Task Gli_step_con_lo_stesso_gruppo_parallelo_partono_insieme()
    {
        var adapter = new ScriptedAdapter();
        adapter.DelayFor[Guid.Empty] = TimeSpan.Zero; // no-op, solo per chiarezza

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var a = Device("a");
        var b = Device("b");
        adapter.DelayFor[a.Id] = TimeSpan.FromMilliseconds(80);
        adapter.DelayFor[b.Id] = TimeSpan.FromMilliseconds(80);

        var steps = new[]
        {
            Step("Spegni A", 10, SequenceStepType.ShutdownDevice, a.Id) with { ParallelGroup = 1 },
            Step("Spegni B", 10, SequenceStepType.ShutdownDevice, b.Id) with { ParallelGroup = 1 }
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [a.Id] = a, [b.Id] = b };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await executor.ExecuteAsync("run-par", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());
        sw.Stop();

        Assert.Equal(RunState.Completed, result.FinalState);
        // Se fossero sequenziali il tempo totale sarebbe >= 160ms; in parallelo resta sotto.
        Assert.True(sw.ElapsedMilliseconds < 150,
            $"Gli step paralleli hanno richiesto {sw.ElapsedMilliseconds}ms: non sembrano concorrenti.");
    }

    [Fact]
    public async Task Un_errore_senza_ContinueOnError_interrompe_gli_step_successivi()
    {
        var adapter = new ScriptedAdapter();
        var app = Device("app");
        var db = Device("db");
        adapter.AlwaysFailFor.Add(app.Id);

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var steps = new[]
        {
            Step("Spegni app", 10, SequenceStepType.ShutdownDevice, app.Id) with { ContinueOnError = false },
            Step("Spegni db", 20, SequenceStepType.ShutdownDevice, db.Id)
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [app.Id] = app, [db.Id] = db };

        var result = await executor.ExecuteAsync("run-2", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Failed, result.FinalState);
        // Il database non deve essere mai stato toccato: non si procede dopo un errore critico.
        Assert.DoesNotContain(adapter.Calls, c => c.DeviceId == db.Id);
    }

    [Fact]
    public async Task Un_errore_con_ContinueOnError_non_blocca_la_sequenza()
    {
        var adapter = new ScriptedAdapter();
        var app = Device("app");
        var db = Device("db");
        adapter.AlwaysFailFor.Add(app.Id);

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var steps = new[]
        {
            Step("Spegni app", 10, SequenceStepType.ShutdownDevice, app.Id) with { ContinueOnError = true },
            Step("Spegni db", 20, SequenceStepType.ShutdownDevice, db.Id)
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [app.Id] = app, [db.Id] = db };

        var result = await executor.ExecuteAsync("run-3", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Failed, result.FinalState); // l'errore resta registrato
        Assert.Contains(adapter.Calls, c => c.DeviceId == db.Id); // ma si è proceduto comunque
    }

    [Fact]
    public async Task Un_fallimento_temporaneo_viene_ritentato_fino_al_limite()
    {
        var adapter = new ScriptedAdapter();
        var device = Device("flaky");
        adapter.FailFirstAttemptFor.Add(device.Id);

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var steps = new[]
        {
            Step("Spegni flaky", 10, SequenceStepType.ShutdownDevice, device.Id) with { MaxRetries = 2 }
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var result = await executor.ExecuteAsync("run-4", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.Contains(result.Actions, a => a.State == ActionState.Failed); // il primo tentativo
        Assert.Contains(result.Actions, a => a.State == ActionState.Succeeded); // il retry
    }

    [Fact]
    public async Task Un_fallimento_persistente_esaurisce_i_tentativi_e_resta_fallito()
    {
        var adapter = new ScriptedAdapter();
        var device = Device("broken");
        adapter.AlwaysFailFor.Add(device.Id);

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var steps = new[]
        {
            Step("Spegni broken", 10, SequenceStepType.ShutdownDevice, device.Id) with { MaxRetries = 1 }
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var result = await executor.ExecuteAsync("run-5", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Failed, result.FinalState);
        // 1 tentativo iniziale + 1 retry = 2 azioni registrate, entrambe fallite.
        Assert.Equal(2, result.Actions.Count);
        Assert.All(result.Actions, a => Assert.Equal(ActionState.Failed, a.State));
    }

    [Fact]
    public async Task Un_timeout_viene_registrato_come_TimedOut_non_come_eccezione()
    {
        var adapter = new ScriptedAdapter();
        var device = Device("slow");
        adapter.DelayFor[device.Id] = TimeSpan.FromSeconds(10);

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var steps = new[]
        {
            Step("Spegni slow", 10, SequenceStepType.ShutdownDevice, device.Id) with
            {
                TimeoutSeconds = 1, MaxRetries = 0
            }
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var result = await executor.ExecuteAsync("run-6", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Failed, result.FinalState);
        Assert.Contains(result.Actions, a => a.State == ActionState.TimedOut);
    }

    [Fact]
    public async Task Un_dispositivo_non_raggiungibile_alla_verifica_non_contraddice_il_successo()
    {
        // Il ping fallito non è prova di arresto, ma nemmeno prova del contrario: l'azione resta
        // riuscita se l'adapter l'ha confermata, con una nota di verifica inconclusiva (§10).
        var adapter = new ScriptedAdapter();
        var device = Device("unreachable-after");
        adapter.StateAfterShutdown[device.Id] = DeviceState.Unreachable;

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var steps = new[]
        {
            Step("Spegni", 10, SequenceStepType.ShutdownDevice, device.Id) with { VerifyCompletion = true }
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var result = await executor.ExecuteAsync("run-7", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Completed, result.FinalState);
        var action = Assert.Single(result.Actions);
        Assert.Equal(ActionState.Succeeded, action.State);
        Assert.Contains("non conclusiva", action.Message);
    }

    [Fact]
    public async Task Una_verifica_in_contraddizione_fa_fallire_l_azione()
    {
        // Il dispositivo risulta ancora acceso dopo il comando di spegnimento: qui l'evidenza
        // contraddice l'azione e deve essere trattata come fallimento, non come successo.
        var adapter = new ScriptedAdapter();
        var device = Device("still-on");
        adapter.StateAfterShutdown[device.Id] = DeviceState.On;

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var steps = new[]
        {
            Step("Spegni", 10, SequenceStepType.ShutdownDevice, device.Id) with
            {
                VerifyCompletion = true, MaxRetries = 0
            }
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var result = await executor.ExecuteAsync("run-8", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Failed, result.FinalState);
        Assert.Contains(result.Actions, a => a.ResultCode == "VERIFICATION_MISMATCH");
    }

    [Fact]
    public async Task Un_azione_gia_completata_viene_saltata_in_modo_idempotente()
    {
        var adapter = new ScriptedAdapter();
        var device = Device("resumed");
        var store = new InMemorySequenceRunStore();

        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());
        var step = Step("Spegni", 10, SequenceStepType.ShutdownDevice, device.Id);
        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        // Simula un riavvio dopo un'esecuzione già completata (§13): la prima esecuzione
        // registra l'azione, la seconda deve saltarla senza richiamare l'adapter.
        await executor.ExecuteAsync("run-resume", SequenceDirection.Shutdown, [step], devices, store);
        adapter.Calls.Clear();

        var result = await executor.ExecuteAsync("run-resume", SequenceDirection.Shutdown, [step],
            devices, store);

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.DoesNotContain(adapter.Calls, c => c.Action == "Shutdown");
        Assert.Contains(result.Actions, a => a.ResultCode == "ALREADY_DONE");
    }

    [Fact]
    public async Task Una_condizione_falsa_salta_lo_step()
    {
        var adapter = new ScriptedAdapter();
        var device = Device("conditional");
        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var step = Step("Spegni se OB", 10, SequenceStepType.ShutdownDevice, device.Id) with
        {
            Condition = ConditionNode.Flag("OB")
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var result = await executor.ExecuteAsync("run-cond", SequenceDirection.Shutdown, [step],
            devices, new InMemorySequenceRunStore(),
            evaluateCondition: _ => ConditionResult.False);

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.Empty(adapter.Calls);
        Assert.Contains(result.Actions, a => a.ResultCode == "CONDITION_FALSE");
    }

    [Fact]
    public async Task Una_condizione_vera_esegue_lo_step()
    {
        var adapter = new ScriptedAdapter();
        var device = Device("conditional-true");
        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var step = Step("Spegni se OB", 10, SequenceStepType.ShutdownDevice, device.Id) with
        {
            Condition = ConditionNode.Flag("OB")
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var result = await executor.ExecuteAsync("run-cond2", SequenceDirection.Shutdown, [step],
            devices, new InMemorySequenceRunStore(),
            evaluateCondition: _ => ConditionResult.True);

        Assert.Equal(RunState.Completed, result.FinalState);
        // Una chiamata di spegnimento più la verifica di completamento (VerifyCompletion di default).
        Assert.Single(adapter.Calls, c => c.Action == "Shutdown");
    }

    [Fact]
    public async Task Il_ritorno_della_corrente_interrompe_al_confine_senza_essere_un_errore()
    {
        var adapter = new ScriptedAdapter();
        var app = Device("app");
        var db = Device("db");
        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var steps = new[]
        {
            Step("Spegni app", 10, SequenceStepType.ShutdownDevice, app.Id),
            Step("Spegni db", 20, SequenceStepType.ShutdownDevice, db.Id)
        };

        var devices = new Dictionary<Guid, ManagedDevice> { [app.Id] = app, [db.Id] = db };

        // Il segnale di interruzione scatta prima del secondo step: riproduce il caso B di §11
        // (corrente tornata mentre la sequenza è in corso, policy StopAtNextStepBoundary).
        var callCount = 0;
        var result = await executor.ExecuteAsync("run-boundary", SequenceDirection.Shutdown, steps,
            devices, new InMemorySequenceRunStore(),
            shouldStopAtBoundary: () => ++callCount > 1);

        Assert.True(result.StoppedAtBoundary);
        Assert.Equal(RunState.Aborted, result.FinalState);
        Assert.DoesNotContain(adapter.Calls, c => c.DeviceId == db.Id);
        Assert.Contains(adapter.Calls, c => c.DeviceId == app.Id);
    }

    [Fact]
    public async Task Lo_step_Notify_non_richiede_un_dispositivo_e_non_puo_fallire_la_sequenza()
    {
        var adapter = new ScriptedAdapter();
        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var step = Step("Notifica", 10, SequenceStepType.Notify);

        var result = await executor.ExecuteAsync("run-notify", SequenceDirection.Shutdown, [step],
            new Dictionary<Guid, ManagedDevice>(), new InMemorySequenceRunStore());

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.Single(result.Actions);
        Assert.Null(result.Actions[0].DeviceId);
    }

    [Fact]
    public async Task Lo_step_ShutdownNutHost_non_esegue_mai_un_arresto_reale()
    {
        // Vincolo assoluto del progetto (§13, §15): questo step non deve avere alcun percorso
        // di codice che tocchi davvero il sistema operativo del mini PC.
        var adapter = new ScriptedAdapter();
        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var step = Step("Arresta mini PC", 99, SequenceStepType.ShutdownNutHost);

        var result = await executor.ExecuteAsync("run-nuthost", SequenceDirection.Shutdown, [step],
            new Dictionary<Guid, ManagedDevice>(), new InMemorySequenceRunStore());

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.Empty(adapter.Calls);
        Assert.Contains(result.Actions, a => a.ResultCode == "HOST_SHUTDOWN_NOOP");
    }

    [Fact]
    public async Task Gli_step_vengono_eseguiti_anche_in_direzione_di_accensione()
    {
        var adapter = new ScriptedAdapter();
        var device = Device("to-start");
        var executor = new SequenceExecutor(new SingleAdapterSelector(adapter), new TestClock());

        var step = Step("Accendi", 10, SequenceStepType.StartupDevice, device.Id);
        var devices = new Dictionary<Guid, ManagedDevice> { [device.Id] = device };

        var result = await executor.ExecuteAsync("run-startup", SequenceDirection.Startup, [step],
            devices, new InMemorySequenceRunStore());

        Assert.Equal(RunState.Completed, result.FinalState);
        Assert.Contains(adapter.Calls, c => c.Action == "Startup");
    }

    [Fact]
    public void Il_raggruppamento_per_esecuzione_preserva_l_ordine_complessivo()
    {
        var a = Step("A", 10, SequenceStepType.ShutdownDevice);
        var b = Step("B", 20, SequenceStepType.ShutdownDevice) with { ParallelGroup = 1 };
        var c = Step("C", 25, SequenceStepType.ShutdownDevice) with { ParallelGroup = 1 };
        var d = Step("D", 30, SequenceStepType.ShutdownDevice);

        var groups = SequenceExecutor.GroupByExecutionOrder([d, c, b, a]).ToList();

        Assert.Equal(3, groups.Count);
        Assert.Equal([a.Id], groups[0].Select(s => s.Id));
        // Il gruppo parallelo contiene esattamente B e C: l'ordine relativo fra i due non è
        // significativo (partono insieme), quindi si verifica l'insieme e non la sequenza.
        Assert.Equal(new HashSet<Guid> { b.Id, c.Id }, groups[1].Select(s => s.Id).ToHashSet());
        Assert.Equal([d.Id], groups[2].Select(s => s.Id));
    }
}
