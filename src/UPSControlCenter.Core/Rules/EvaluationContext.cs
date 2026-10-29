using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Core.Rules;

/// <summary>Tutto ciò che serve a valutare le regole in un istante. Immutabile e testabile.</summary>
public sealed record EvaluationContext
{
    public required UpsSnapshot Snapshot { get; init; }
    public required ShutdownPolicy Policy { get; init; }
    public BlackoutState? Blackout { get; init; }

    /// <summary>Stato accettato dopo il debounce, non la lettura grezza.</summary>
    public required UpsStatusFlags DebouncedStatus { get; init; }

    public required DateTimeOffset NowUtc { get; init; }
    public required long NowMonotonicMs { get; init; }
    public required string BootId { get; init; }

    /// <summary>Età dell'ultimo heartbeat dell'orchestrator; null se mai visto.</summary>
    public TimeSpan? OrchestratorHeartbeatAge { get; init; }

    /// <summary>Vero se il kill switch è attivo: in tal caso nessuna azione è ammessa (§8.3).</summary>
    public bool AutomationDisabled { get; init; }

    /// <summary>Durata del blackout in corso, calcolata con il tempo monotono quando possibile.</summary>
    public TimeSpan? BlackoutDuration =>
        Blackout is { IsOpen: true } b
            ? b.GetDuration(NowMonotonicMs, BootId, NowUtc)
            : null;
}

/// <summary>Esito della valutazione: cosa fare e, soprattutto, perché.</summary>
public sealed record RuleDecision
{
    /// <summary>Vero se va avviata una sequenza di spegnimento.</summary>
    public required bool ShouldTriggerShutdown { get; init; }

    public Guid? TriggeredRuleId { get; init; }
    public string? TriggeredRuleName { get; init; }
    public Guid? SequenceId { get; init; }

    /// <summary>Motivazione leggibile, registrata nell'audit e mostrata nella timeline.</summary>
    public required string Reason { get; init; }

    /// <summary>Condizioni critiche rilevate: gestite separatamente dalle regole ordinarie (§9.3).</summary>
    public IReadOnlyList<UpsEventType> CriticalConditions { get; init; } = [];

    /// <summary>Secondi stimati alla prossima soglia, per la dashboard. Null se non calcolabile.</summary>
    public int? SecondsToNextThreshold { get; init; }

    /// <summary>Vero se la decisione si basa su dati incompleti o incerti: va mostrato nella UI (§5.3).</summary>
    public bool BasedOnUncertainData { get; init; }

    public static RuleDecision NoAction(string reason, IReadOnlyList<UpsEventType>? critical = null,
        int? secondsToThreshold = null, bool uncertain = false) => new()
    {
        ShouldTriggerShutdown = false,
        Reason = reason,
        CriticalConditions = critical ?? [],
        SecondsToNextThreshold = secondsToThreshold,
        BasedOnUncertainData = uncertain
    };
}
