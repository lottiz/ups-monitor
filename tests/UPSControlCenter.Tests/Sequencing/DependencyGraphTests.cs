using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Sequencing;

namespace UPSControlCenter.Tests.Sequencing;

/// <summary>
/// Grafo delle dipendenze (§10): aciclicità, ordinamento topologico e simmetria fra spegnimento
/// e accensione.
/// </summary>
public sealed class DependencyGraphTests
{
    private static DeviceDependency Dep(Guid dependent, Guid dependsOn,
        DependencyType type = DependencyType.Generic) => new()
        {
            DependentDeviceId = dependent,
            DependsOnDeviceId = dependsOn,
            DependencyType = type,
            IsHard = true
        };

    [Fact]
    public void Un_grafo_senza_cicli_e_valido()
    {
        var app = Guid.CreateVersion7();
        var dbServer = Guid.CreateVersion7();
        var nas = Guid.CreateVersion7();

        var graph = new DependencyGraph([app, dbServer, nas],
        [
            Dep(app, dbServer, DependencyType.Database),
            Dep(dbServer, nas, DependencyType.StorageNfs)
        ]);

        Assert.True(graph.Validate().IsValid);
    }

    [Fact]
    public void Un_ciclo_diretto_viene_rilevato_e_descritto()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var graph = new DependencyGraph([a, b], [Dep(a, b), Dep(b, a)]);
        var result = graph.Validate();

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Cycles);
        Assert.Contains(result.Errors, e => e.Contains("circolare"));
    }

    [Fact]
    public void Un_ciclo_indiretto_su_tre_nodi_viene_rilevato()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();

        var graph = new DependencyGraph([a, b, c], [Dep(a, b), Dep(b, c), Dep(c, a)]);

        Assert.False(graph.Validate().IsValid);
    }

    [Fact]
    public void L_ordinamento_di_spegnimento_tratta_prima_i_consumatori()
    {
        // app → database → NAS. Spegnendo si parte dall'app e si finisce col NAS, altrimenti
        // si spegnerebbe lo storage sotto i piedi di chi lo usa (§10).
        var app = Guid.CreateVersion7();
        var dbServer = Guid.CreateVersion7();
        var nas = Guid.CreateVersion7();

        var graph = new DependencyGraph([app, dbServer, nas],
        [
            Dep(app, dbServer, DependencyType.Database),
            Dep(dbServer, nas, DependencyType.StorageNfs)
        ]);

        var order = graph.TopologicalOrderForShutdown();

        Assert.Equal([app, dbServer, nas], order);
    }

    [Fact]
    public void L_ordinamento_di_accensione_e_l_inverso_di_quello_di_spegnimento()
    {
        var app = Guid.CreateVersion7();
        var dbServer = Guid.CreateVersion7();
        var nas = Guid.CreateVersion7();

        var graph = new DependencyGraph([app, dbServer, nas],
        [
            Dep(app, dbServer, DependencyType.Database),
            Dep(dbServer, nas, DependencyType.StorageNfs)
        ]);

        var shutdown = graph.TopologicalOrderForShutdown();
        var startup = graph.TopologicalOrderForStartup();

        Assert.Equal(shutdown.Reverse(), startup);
        Assert.Equal(nas, startup[0]);       // prima lo storage
        Assert.Equal(app, startup[^1]);      // poi chi lo usa
    }

    [Fact]
    public void L_ordinamento_rispetta_la_priorita_fra_nodi_indipendenti()
    {
        var urgent = Guid.CreateVersion7();
        var later = Guid.CreateVersion7();

        var graph = new DependencyGraph([urgent, later], []);

        var priorities = new Dictionary<Guid, int> { [urgent] = 10, [later] = 900 };
        var order = graph.TopologicalOrderForShutdown(id => priorities[id]);

        Assert.Equal(urgent, order[0]);
        Assert.Equal(later, order[1]);
    }

    [Fact]
    public void L_ordinamento_e_deterministico_fra_esecuzioni_successive()
    {
        // Un ordine non deterministico renderebbe le sequenze non riproducibili e le
        // simulazioni inaffidabili come previsione del comportamento reale.
        var nodes = Enumerable.Range(0, 8).Select(_ => Guid.CreateVersion7()).ToList();
        var deps = new List<DeviceDependency>
        {
            Dep(nodes[0], nodes[3]),
            Dep(nodes[1], nodes[3]),
            Dep(nodes[2], nodes[4]),
            Dep(nodes[3], nodes[5])
        };

        var first = new DependencyGraph(nodes, deps).TopologicalOrderForShutdown();
        var second = new DependencyGraph(nodes, deps).TopologicalOrderForShutdown();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Ordinare_un_grafo_ciclico_solleva_un_errore_esplicito()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var graph = new DependencyGraph([a, b], [Dep(a, b), Dep(b, a)]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.TopologicalOrderForShutdown());

        Assert.Contains("circolari", ex.Message);
    }

    [Fact]
    public void Le_dipendenze_verso_nodi_esterni_estendono_il_grafo()
    {
        // Un dispositivo citato solo come dipendenza deve comunque entrare nell'ordinamento,
        // altrimenti verrebbe silenziosamente ignorato dalla sequenza.
        var known = Guid.CreateVersion7();
        var external = Guid.CreateVersion7();

        var graph = new DependencyGraph([known], [Dep(known, external)]);

        Assert.Contains(external, graph.Nodes);
        Assert.Equal(2, graph.TopologicalOrderForShutdown().Count);
    }
}
