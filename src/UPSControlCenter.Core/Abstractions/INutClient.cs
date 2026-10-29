using UPSControlCenter.Core.Nut;

namespace UPSControlCenter.Core.Abstractions;

/// <summary>
/// Accesso in <b>sola lettura</b> a Network UPS Tools.
/// <para>
/// Non esiste e non deve esistere alcun metodo di controllo (FSD, killpower, set di variabili,
/// modifica di configurazione): §15 lo vieta e l'assenza dall'interfaccia lo rende impossibile
/// per costruzione, non per convenzione.
/// </para>
/// </summary>
public interface INutClient
{
    /// <summary>Elenca gli UPS configurati sul server NUT.</summary>
    Task<IReadOnlyList<string>> ListUpsAsync(CancellationToken ct = default);

    /// <summary>
    /// Legge tutte le variabili di un UPS e le normalizza. Non solleva eccezioni per problemi di
    /// comunicazione: restituisce uno snapshot con <c>CommunicationOk = false</c>.
    /// </summary>
    Task<UpsSnapshot> ReadAsync(string upsName, CancellationToken ct = default);
}
