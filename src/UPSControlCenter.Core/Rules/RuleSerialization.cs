using System.Text.Json;
using System.Text.Json.Serialization;

namespace UPSControlCenter.Core.Rules;

/// <summary>Serializzazione delle regole. Opzioni condivise per garantire checksum stabili (§3.3).</summary>
public static class RuleSerialization
{
    /// <summary>
    /// Opzioni deterministiche: senza indentazione e con enum come stringhe, così il JSON di una
    /// stessa configurazione è byte-identico e il checksum resta confrontabile.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string SerializeCondition(ConditionNode node) =>
        JsonSerializer.Serialize(node, Options);

    /// <summary>Deserializza un albero di condizioni. Restituisce null se il JSON non è valido.</summary>
    public static ConditionNode? DeserializeCondition(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try
        {
            return JsonSerializer.Deserialize<ConditionNode>(json, Options);
        }
        catch (JsonException)
        {
            // Una regola illeggibile non deve far cadere il motore: viene ignorata e segnalata altrove.
            return null;
        }
    }

    public static string SerializeTimeWindows(IEnumerable<TimeWindow> windows) =>
        JsonSerializer.Serialize(windows.Select(w => $"{w.Start:HH\\:mm}-{w.End:HH\\:mm}"), Options);

    public static IReadOnlyList<TimeWindow> DeserializeTimeWindows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
            return [.. raw.Select(TimeWindow.TryParse).OfType<TimeWindow>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
