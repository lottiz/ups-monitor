using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Contracts.Dto;

/// <summary>Riga dell'elenco regole (§18).</summary>
public sealed record ShutdownRuleListItemDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required int Priority { get; init; }
    public required bool IsEnabled { get; init; }
    public string? TriggerSequenceName { get; init; }
    public bool HasTimeWindows { get; init; }
    public bool HasDayRestriction { get; init; }
}

/// <summary>
/// Riga semplice del builder visuale: una singola condizione leaf. L'editor compone un albero
/// con un solo operatore di primo livello (AND/OR) su un elenco di queste righe (§9.2). Alberi
/// più complessi (NOT, gruppi annidati) restano modificabili tramite la modalità avanzata (JSON).
/// </summary>
public sealed record SimpleConditionRowDto
{
    public required ConditionKind Condition { get; init; }
    public ConditionOperator Operator { get; init; } = ConditionOperator.GreaterThanOrEqual;
    public decimal? NumericValue { get; init; }
    public string? TextValue { get; init; }
}

public sealed record ShutdownRuleFormDto
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public int Priority { get; init; } = 500;
    public bool IsEnabled { get; init; } = true;
    public Guid? TriggerSequenceId { get; init; }

    /// <summary>Operatore di primo livello per la modalità semplice.</summary>
    public NodeKind TopOperator { get; init; } = NodeKind.And;
    // List<T> e non IReadOnlyList<T>: il model binder MVC deve poter istanziare e popolare la
    // collezione per i campi indicizzati "SimpleConditions[0].Condition" generati dal form; con
    // un'interfaccia il binder non sa quale tipo concreto costruire e la lista resta vuota.
    public List<SimpleConditionRowDto> SimpleConditions { get; init; } = [];

    /// <summary>Se vera, <see cref="RawConditionJson"/> prevale sul builder semplice.</summary>
    public bool UseAdvancedMode { get; init; }
    public string? RawConditionJson { get; init; }

    /// <summary>Fasce orarie in formato "HH:mm-HH:mm", una per riga.</summary>
    public string? TimeWindowsText { get; init; }

    /// <summary>Giorni della settimana selezionati (0=domenica … 6=sabato). Vuoto = tutti.</summary>
    public List<int> SelectedDays { get; init; } = [];
}
