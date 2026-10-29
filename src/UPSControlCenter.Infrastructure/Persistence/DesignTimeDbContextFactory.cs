using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UPSControlCenter.Infrastructure.Persistence;

/// <summary>
/// Factory usata dagli strumenti EF Core per generare le migrazioni.
/// <para>
/// Non si connette a un database reale: la stringa è un segnaposto, perché la generazione delle
/// migrazioni richiede solo il modello. Così le migrazioni sono producibili senza avere un
/// PostgreSQL in esecuzione né segreti a disposizione.
/// </para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<UpsDbContext>
{
    public UpsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("UPS_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=upscc_design;Username=design;Password=design";

        var options = new DbContextOptionsBuilder<UpsDbContext>()
            .UseNpgsql(connectionString, npg => npg
                .MigrationsHistoryTable("__ef_migrations_history", UpsDbContext.Schema))
            // Colonne, indici e vincoli in snake_case: i filtri degli indici parziali e i
            // check constraint sono scritti in snake_case e con i nomi PascalCase quotati di
            // default non verrebbero risolti da PostgreSQL.
            .UseSnakeCaseNamingConvention()
            .Options;

        return new UpsDbContext(options);
    }
}
