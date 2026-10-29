using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UPSControlCenter.Infrastructure.Persistence;
using UPSControlCenter.Web.Services;
using UPSControlCenter.Web.ViewModels;

namespace UPSControlCenter.Web.Controllers;

[Authorize(Policy = Policies.ViewDashboard)]
public sealed class DashboardController(DashboardStateService state) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = new DashboardViewModel
        {
            UpsList = await state.GetUpsStatusAsync(ct),
            Infrastructure = await state.GetInfrastructureStatusAsync(ct),
            Automation = await state.GetAutomationStatusAsync(ct)
        };

        return View(model);
    }
}
