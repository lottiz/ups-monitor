using System.Text.Json.Serialization;

namespace UPSControlCenter.Core.Rules;

public enum ConditionOperator
{
    Equals = 0,
    NotEquals = 1,
    GreaterThan = 2,
    GreaterThanOrEqual = 3,
    LessThan = 4,
    LessThanOrEqual = 5
}

public enum ConditionKind
{
    /// <summary>Durata del blackout in secondi.</summary>
    BlackoutDurationSeconds = 0,
    /// <summary>Presenza di un flag di stato NUT (valore = nome flag, es. "OB", "LB").</summary>
    UpsStatusFlag = 1,
    /// <summary>Autonomia residua in secondi. Può essere sconosciuta.</summary>
    RemainingRuntimeSeconds = 2,
    /// <summary>Carica batteria percentuale. Può essere sconosciuta.</summary>
    BatteryChargePercent = 3,
    /// <summary>Carico UPS percentuale.</summary>
    UpsLoadPercent = 4,
    /// <summary>Vero se l'ora corrente ricade nella fascia indicata ("HH:mm-HH:mm").</summary>
    TimeWindow = 5,
    /// <summary>Bitmask dei giorni della settimana ammessi.</summary>
    DayOfWeekMask = 6,
    /// <summary>Vero se la comunicazione con NUT è persa.</summary>
    CommunicationLost = 7,
    /// <summary>Vero se i dati sono obsoleti oltre la soglia.</summary>
    DataStale = 8,
    /// <summary>Vero se la durata del blackout non è ricostruibile con certezza.</summary>
    BlackoutDurationUncertain = 9
}

public enum NodeKind
{
    And = 0,
    Or = 1,
    Not = 2,
    Leaf = 3
}

/// <summary>
/// Nodo dell'albero di condizioni. Supporta AND, OR, NOT e foglie tipizzate (§9.2).
/// </summary>
public sealed class ConditionNode
{
    public NodeKind Kind { get; set; } = NodeKind.Leaf;

    /// <summary>Figli, per i nodi AND/OR/NOT.</summary>
    public List<ConditionNode> Children { get; set; } = [];

    // --- campi usati solo dalle foglie ---
    public ConditionKind? Condition { get; set; }
    public ConditionOperator Operator { get; set; } = ConditionOperator.GreaterThanOrEqual;

    /// <summary>Valore numerico di confronto, per le condizioni quantitative.</summary>
    public decimal? NumericValue { get; set; }

    /// <summary>Valore testuale, per i flag di stato e le fasce orarie.</summary>
    public string? TextValue { get; set; }

    [JsonIgnore]
    public bool IsLeaf => Kind == NodeKind.Leaf;

    public static ConditionNode And(params ConditionNode[] children) =>
        new() { Kind = NodeKind.And, Children = [.. children] };

    public static ConditionNode Or(params ConditionNode[] children) =>
        new() { Kind = NodeKind.Or, Children = [.. children] };

    public static ConditionNode Not(ConditionNode child) =>
        new() { Kind = NodeKind.Not, Children = [child] };

    public static ConditionNode Numeric(ConditionKind kind, ConditionOperator op, decimal value) =>
        new() { Kind = NodeKind.Leaf, Condition = kind, Operator = op, NumericValue = value };

    public static ConditionNode Flag(string nutFlag) =>
        new() { Kind = NodeKind.Leaf, Condition = ConditionKind.UpsStatusFlag, TextValue = nutFlag };

    public static ConditionNode Boolean(ConditionKind kind) =>
        new() { Kind = NodeKind.Leaf, Condition = kind };
}
