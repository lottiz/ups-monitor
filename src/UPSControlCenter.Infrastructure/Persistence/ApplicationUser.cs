using Microsoft.AspNetCore.Identity;

namespace UPSControlCenter.Infrastructure.Persistence;

/// <summary>Utente del portale. Identity su PostgreSQL (§4).</summary>
public class ApplicationUser : IdentityUser
{
    public string? FullName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Ruoli applicativi (§4).</summary>
public static class ApplicationRoles
{
    public const string Administrator = "Administrator";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Administrator, Operator, Viewer];
}

/// <summary>
/// Policy di autorizzazione nominate. Usare policy invece di controlli di ruolo sparsi rende
/// le capability modificabili in un unico punto (§8.6).
/// </summary>
public static class Policies
{
    public const string ViewDashboard = "ViewDashboard";
    public const string RunSimulation = "RunSimulation";
    public const string ManageDevices = "ManageDevices";
    public const string EditRules = "EditRules";
    public const string PublishConfiguration = "PublishConfiguration";
    public const string ManageCredentials = "ManageCredentials";
    public const string EnableLive = "EnableLive";
    public const string UseKillSwitch = "UseKillSwitch";
    public const string ManageUsers = "ManageUsers";
}
