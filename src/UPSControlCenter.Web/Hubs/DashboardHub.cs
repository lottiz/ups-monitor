using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using UPSControlCenter.Contracts.Dto;

namespace UPSControlCenter.Web.Hubs;

/// <summary>
/// Hub per gli aggiornamenti in tempo reale della dashboard (§5.3).
/// Richiede autenticazione: lo stato dell'infrastruttura non è informazione pubblica.
/// </summary>
[Authorize]
public sealed class DashboardHub : Hub<IDashboardClient>
{
}

/// <summary>
/// Contratto tipizzato verso il client. Tipizzarlo evita di propagare nomi di metodo come
/// stringhe fra server e JavaScript.
/// </summary>
public interface IDashboardClient
{
    Task UpsStatusChanged(UpsStatusDto status);
    Task AutomationStatusChanged(AutomationStatusDto status);
    Task InfrastructureStatusChanged(InfrastructureStatusDto status);
}
