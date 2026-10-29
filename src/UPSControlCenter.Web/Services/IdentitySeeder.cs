using Microsoft.AspNetCore.Identity;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Web.Services;

/// <summary>Crea i ruoli e, se richiesto, il primo amministratore.</summary>
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration,
        ILogger logger)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in ApplicationRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
                logger.LogInformation("Ruolo «{Role}» creato.", role);
            }
        }

        var email = configuration["Bootstrap:AdminEmail"];
        var password = configuration["Bootstrap:AdminPassword"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            // Nessun amministratore predefinito: una password di default sarebbe una porta
            // aperta, e un valore noto nel repository lo sarebbe ancora di più (§15).
            logger.LogWarning(
                "Nessun amministratore iniziale configurato. Impostare Bootstrap__AdminEmail e " +
                "Bootstrap__AdminPassword per creare il primo utente, poi rimuoverli dall'ambiente.");
            return;
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            logger.LogInformation("L'amministratore {Email} esiste già: nessuna azione.", email);
            return;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = "Amministratore iniziale",
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };

        var result = await userManager.CreateAsync(user, password);

        if (!result.Succeeded)
        {
            // Gli errori di Identity non contengono la password, quindi sono registrabili.
            logger.LogError("Creazione dell'amministratore non riuscita: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(user, ApplicationRoles.Administrator);

        logger.LogWarning(
            "Amministratore iniziale {Email} creato. Rimuovere ora Bootstrap__AdminPassword " +
            "dall'ambiente e cambiare la password al primo accesso.", email);
    }
}
