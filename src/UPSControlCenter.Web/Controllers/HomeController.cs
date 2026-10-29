using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using UPSControlCenter.Web.Models;

namespace UPSControlCenter.Web.Controllers;

public sealed class HomeController : Controller
{
    /// <summary>La home reindirizza alla dashboard: il portale non ha una pagina vetrina.</summary>
    public IActionResult Index() => RedirectToAction("Index", "Dashboard");

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
