using UPSControlCenter.Core.Entities;

namespace UPSControlCenter.Core.Sequencing;

/// <summary>Esito della validazione di un grafo di dipendenze.</summary>
public sealed record GraphValidationResult
{
    public required bool IsValid { get; init; }

    /// <summary>Cicli rilevati, ciascuno come sequenza di nodi che si richiude su se stessa.</summary>
    public IReadOnlyList<IReadOnlyList<Guid>> Cycles { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static GraphValidationResult Valid() => new() { IsValid = true };
}

/// <summary>
/// Grafo aciclico delle dipendenze fra dispositivi (§10).
/// <para>
/// Convenzione: un arco <c>A → B</c> significa "A dipende da B". Nello <b>spegnimento</b> A va
/// trattato prima di B (si spengono prima i consumatori); nell'<b>accensione</b> l'ordine è
/// invertito (prima lo storage, poi chi lo usa).
/// </para>
/// </summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<Guid, HashSet<Guid>> _dependsOn = [];
    private readonly HashSet<Guid> _nodes = [];

    public DependencyGraph(IEnumerable<Guid> nodes, IEnumerable<DeviceDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(dependencies);

        foreach (var n in nodes)
        {
            _nodes.Add(n);
            _dependsOn.TryAdd(n, []);
        }

        foreach (var d in dependencies)
        {
            // Nodi fuori dall'insieme considerato vengono aggiunti: il grafo resta completo.
            _nodes.Add(d.DependentDeviceId);
            _nodes.Add(d.DependsOnDeviceId);
            _dependsOn.TryAdd(d.DependentDeviceId, []);
            _dependsOn.TryAdd(d.DependsOnDeviceId, []);
            _dependsOn[d.DependentDeviceId].Add(d.DependsOnDeviceId);
        }
    }

    public IReadOnlySet<Guid> Nodes => _nodes;

    public IReadOnlySet<Guid> DependenciesOf(Guid node) =>
        _dependsOn.TryGetValue(node, out var set) ? set : new HashSet<Guid>();

    /// <summary>
    /// Verifica l'aciclicità con una DFS a tre colori, riportando i cicli effettivi e non solo
    /// la loro esistenza: l'editor deve poter indicare all'utente quali nodi li compongono.
    /// </summary>
    public GraphValidationResult Validate()
    {
        var color = new Dictionary<Guid, int>(); // 0 non visitato, 1 in corso, 2 completato
        var stack = new List<Guid>();
        var cycles = new List<IReadOnlyList<Guid>>();

        foreach (var node in _nodes)
        {
            if (!color.ContainsKey(node))
                Visit(node, color, stack, cycles);
        }

        if (cycles.Count == 0) return GraphValidationResult.Valid();

        var errors = cycles
            .Select(c => $"Dipendenza circolare rilevata: {string.Join(" → ", c)}")
            .ToList();

        return new GraphValidationResult { IsValid = false, Cycles = cycles, Errors = errors };
    }

    private void Visit(Guid node, Dictionary<Guid, int> color, List<Guid> stack,
        List<IReadOnlyList<Guid>> cycles)
    {
        color[node] = 1;
        stack.Add(node);

        foreach (var next in DependenciesOf(node))
        {
            var state = color.GetValueOrDefault(next, 0);
            if (state == 1)
            {
                // Arco verso un nodo ancora in esplorazione: il ciclo è il tratto di stack da lì.
                var idx = stack.IndexOf(next);
                if (idx >= 0)
                    cycles.Add([.. stack[idx..], next]);
            }
            else if (state == 0)
            {
                Visit(next, color, stack, cycles);
            }
        }

        stack.RemoveAt(stack.Count - 1);
        color[node] = 2;
    }

    /// <summary>
    /// Ordinamento topologico per lo spegnimento: un nodo compare prima di tutti quelli da cui
    /// dipende. A parità di vincoli l'ordine segue <paramref name="priority"/> e poi l'id, così
    /// il risultato è deterministico e riproducibile fra esecuzioni.
    /// </summary>
    public IReadOnlyList<Guid> TopologicalOrderForShutdown(Func<Guid, int>? priority = null)
    {
        var validation = Validate();
        if (!validation.IsValid)
            throw new InvalidOperationException(
                "Impossibile ordinare un grafo con dipendenze circolari: " +
                string.Join("; ", validation.Errors));

        priority ??= _ => 500;

        // Kahn: si parte dai nodi da cui nessuno dipende... per lo spegnimento serve l'opposto,
        // cioè i nodi che non sono dipendenza di nessun altro (i "consumatori" finali).
        var dependents = _nodes.ToDictionary(n => n, _ => 0);
        foreach (var deps in _dependsOn.Values)
        {
            foreach (var d in deps)
            {
                // L'arco node → d contribuisce al numero di dipendenti di 'd'.
                dependents[d] = dependents.GetValueOrDefault(d) + 1;
            }
        }

        var ready = new List<Guid>(_nodes.Where(n => dependents[n] == 0));
        var result = new List<Guid>(_nodes.Count);
        var remaining = new Dictionary<Guid, int>(dependents);

        while (ready.Count > 0)
        {
            ready.Sort((a, b) =>
            {
                var cmp = priority(a).CompareTo(priority(b));
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            var current = ready[0];
            ready.RemoveAt(0);
            result.Add(current);

            foreach (var dep in DependenciesOf(current))
            {
                remaining[dep]--;
                if (remaining[dep] == 0) ready.Add(dep);
            }
        }

        if (result.Count != _nodes.Count)
            throw new InvalidOperationException(
                "Ordinamento topologico incompleto: il grafo contiene cicli non rilevati.");

        return result;
    }

    /// <summary>Ordine di accensione: esattamente l'inverso dello spegnimento (§12).</summary>
    public IReadOnlyList<Guid> TopologicalOrderForStartup(Func<Guid, int>? priority = null) =>
        [.. TopologicalOrderForShutdown(priority).Reverse()];
}
