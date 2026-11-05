using System.Reflection;
using UPSControlCenter.Contracts.Dto;

namespace UPSControlCenter.Tests.Adapters;

/// <summary>
/// Verifica un vincolo di sicurezza per i form MVC che non è imposto dal compilatore:
/// <para>
/// Il model binder di ASP.NET Core per form con <c>application/x-www-form-urlencoded</c> (quelli
/// generati da <c>asp-for</c>) non riesce a popolare una proprietà di collezione dichiarata come
/// interfaccia (<c>IReadOnlyList&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>): non sa quale tipo
/// concreto istanziare per i campi indicizzati come <c>SimpleConditions[0].Condition</c>. Il
/// binding silenziosamente lascia la proprietà vuota, senza generare un errore di compilazione
/// né un errore a runtime — l'unico sintomo è un salvataggio che "non funziona" (§21).
/// </para>
/// <para>
/// Questo test ha trovato esattamente questo bug in <see cref="ShutdownRuleFormDto"/> durante la
/// verifica manuale dell'editor delle regole: il form inviava le condizioni ma il server le
/// vedeva come lista vuota. Fissa la lezione imparata perché non si riproponga.
/// </para>
/// </summary>
public sealed class ModelBindingSafetyTests
{
    [Fact]
    public void ShutdownRuleFormDto_ha_collezioni_bindabili_dal_form_MVC()
    {
        AssertBindableCollections(typeof(ShutdownRuleFormDto));
    }

    [Fact]
    public void SequenceHeaderFormDto_ha_collezioni_bindabili_dal_form_MVC()
    {
        AssertBindableCollections(typeof(SequenceHeaderFormDto));
    }

    /// <summary>
    /// Verifica che ogni proprietà di tipo collezione sia un tipo concreto istanziabile
    /// (<c>List&lt;T&gt;</c>, array) e non un'interfaccia: è l'unica forma che il model binder
    /// dei form MVC sa popolare per i campi indicizzati generati da un form HTML.
    /// </summary>
    private static void AssertBindableCollections(Type formDtoType)
    {
        var offending = new List<string>();

        foreach (var prop in formDtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType == typeof(string)) continue;
            if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType)) continue;

            var isConcreteList = prop.PropertyType.IsArray
                || (prop.PropertyType.IsGenericType
                    && prop.PropertyType.GetGenericTypeDefinition() == typeof(List<>));

            if (!isConcreteList)
                offending.Add($"{formDtoType.Name}.{prop.Name} è {prop.PropertyType.Name}: " +
                               "il model binder dei form non può popolarla. Usare List<T>.");
        }

        Assert.Empty(offending);
    }
}
