using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Core.Rules;

/// <summary>
/// Esito a tre valori. <see cref="Unknown"/> è indispensabile: una condizione su un dato
/// mancante non è né vera né falsa, e collassarla su <c>false</c> nasconderebbe l'incertezza (§9.3).
/// </summary>
public enum ConditionResult
{
    False = 0,
    True = 1,
    Unknown = 2
}

/// <summary>Valuta un albero di condizioni con logica di Kleene a tre valori.</summary>
public static class ConditionEvaluator
{
    public static ConditionResult Evaluate(ConditionNode node, EvaluationContext ctx)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(ctx);

        return node.Kind switch
        {
            NodeKind.And => EvaluateAnd(node, ctx),
            NodeKind.Or => EvaluateOr(node, ctx),
            NodeKind.Not => Negate(node.Children.Count == 1
                ? Evaluate(node.Children[0], ctx)
                : ConditionResult.Unknown),
            NodeKind.Leaf => EvaluateLeaf(node, ctx),
            _ => ConditionResult.Unknown
        };
    }

    /// <summary>AND di Kleene: un solo <c>False</c> rende falso il tutto, anche in presenza di ignoti.</summary>
    private static ConditionResult EvaluateAnd(ConditionNode node, EvaluationContext ctx)
    {
        if (node.Children.Count == 0) return ConditionResult.Unknown;

        var sawUnknown = false;
        foreach (var child in node.Children)
        {
            var r = Evaluate(child, ctx);
            if (r == ConditionResult.False) return ConditionResult.False;
            if (r == ConditionResult.Unknown) sawUnknown = true;
        }
        return sawUnknown ? ConditionResult.Unknown : ConditionResult.True;
    }

    /// <summary>OR di Kleene: un solo <c>True</c> rende vero il tutto.</summary>
    private static ConditionResult EvaluateOr(ConditionNode node, EvaluationContext ctx)
    {
        if (node.Children.Count == 0) return ConditionResult.Unknown;

        var sawUnknown = false;
        foreach (var child in node.Children)
        {
            var r = Evaluate(child, ctx);
            if (r == ConditionResult.True) return ConditionResult.True;
            if (r == ConditionResult.Unknown) sawUnknown = true;
        }
        return sawUnknown ? ConditionResult.Unknown : ConditionResult.False;
    }

    private static ConditionResult Negate(ConditionResult r) => r switch
    {
        ConditionResult.True => ConditionResult.False,
        ConditionResult.False => ConditionResult.True,
        _ => ConditionResult.Unknown
    };

    private static ConditionResult EvaluateLeaf(ConditionNode node, EvaluationContext ctx)
    {
        if (node.Condition is not { } kind) return ConditionResult.Unknown;

        switch (kind)
        {
            case ConditionKind.BlackoutDurationSeconds:
                return ctx.BlackoutDuration is { } d
                    ? Compare((decimal)d.TotalSeconds, node)
                    : ConditionResult.Unknown;

            case ConditionKind.UpsStatusFlag:
                if (string.IsNullOrWhiteSpace(node.TextValue)) return ConditionResult.Unknown;
                if (ctx.DebouncedStatus.IsUnknown()) return ConditionResult.Unknown;
                var wanted = UpsStatusParser.Parse(node.TextValue);
                if (wanted.IsUnknown()) return ConditionResult.Unknown;
                return ctx.DebouncedStatus.HasFlag(wanted) ? ConditionResult.True : ConditionResult.False;

            case ConditionKind.RemainingRuntimeSeconds:
                // Valore assente: Unknown, mai zero.
                return ctx.Snapshot.BatteryRuntimeSeconds is { } rt
                    ? Compare(rt, node)
                    : ConditionResult.Unknown;

            case ConditionKind.BatteryChargePercent:
                return ctx.Snapshot.BatteryChargePercent is { } charge
                    ? Compare(charge, node)
                    : ConditionResult.Unknown;

            case ConditionKind.UpsLoadPercent:
                return ctx.Snapshot.LoadPercent is { } load
                    ? Compare(load, node)
                    : ConditionResult.Unknown;

            case ConditionKind.TimeWindow:
                if (string.IsNullOrWhiteSpace(node.TextValue)) return ConditionResult.Unknown;
                var window = TimeWindow.TryParse(node.TextValue);
                if (window is null) return ConditionResult.Unknown;
                return window.Contains(TimeOnly.FromDateTime(ctx.NowUtc.UtcDateTime))
                    ? ConditionResult.True : ConditionResult.False;

            case ConditionKind.DayOfWeekMask:
                if (node.NumericValue is not { } maskValue) return ConditionResult.Unknown;
                var bit = 1 << (int)ctx.NowUtc.DayOfWeek;
                return ((int)maskValue & bit) != 0 ? ConditionResult.True : ConditionResult.False;

            case ConditionKind.CommunicationLost:
                return ctx.Snapshot.CommunicationOk ? ConditionResult.False : ConditionResult.True;

            case ConditionKind.DataStale:
                return ctx.Snapshot.IsStale ? ConditionResult.True : ConditionResult.False;

            case ConditionKind.BlackoutDurationUncertain:
                if (ctx.Blackout is null) return ConditionResult.Unknown;
                return ctx.Blackout.DurationCertain ? ConditionResult.False : ConditionResult.True;

            default:
                return ConditionResult.Unknown;
        }
    }

    private static ConditionResult Compare(decimal actual, ConditionNode node)
    {
        if (node.NumericValue is not { } expected) return ConditionResult.Unknown;

        var outcome = node.Operator switch
        {
            ConditionOperator.Equals => actual == expected,
            ConditionOperator.NotEquals => actual != expected,
            ConditionOperator.GreaterThan => actual > expected,
            ConditionOperator.GreaterThanOrEqual => actual >= expected,
            ConditionOperator.LessThan => actual < expected,
            ConditionOperator.LessThanOrEqual => actual <= expected,
            _ => false
        };
        return outcome ? ConditionResult.True : ConditionResult.False;
    }
}

/// <summary>Fascia oraria, con supporto per gli intervalli che attraversano la mezzanotte.</summary>
public sealed record TimeWindow(TimeOnly Start, TimeOnly End)
{
    public bool Contains(TimeOnly value) =>
        Start <= End
            ? value >= Start && value <= End
            : value >= Start || value <= End; // es. 22:00-06:00

    /// <summary>Interpreta il formato <c>"HH:mm-HH:mm"</c>. Restituisce null se non valido.</summary>
    public static TimeWindow? TryParse(string text)
    {
        var parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        if (!TimeOnly.TryParse(parts[0], out var start)) return null;
        if (!TimeOnly.TryParse(parts[1], out var end)) return null;
        return new TimeWindow(start, end);
    }
}
