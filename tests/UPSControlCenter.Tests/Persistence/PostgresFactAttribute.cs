namespace UPSControlCenter.Tests.Persistence;

/// <summary>
/// Marca un test che richiede un PostgreSQL reale.
/// <para>
/// Se <c>UPS_TEST_CONNECTION</c> non è impostata, il test viene <b>saltato</b> e non passato:
/// un test che passa vacuamente quando il database non c'è darebbe la falsa impressione che i
/// vincoli siano stati verificati.
/// </para>
/// <example>
/// <code>
/// UPS_TEST_CONNECTION="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres" \
///   dotnet test
/// </code>
/// </example>
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "UPS_TEST_CONNECTION";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = $"{ConnectionVariable} non impostata: test di integrazione PostgreSQL saltato. " +
                   "Vedi docs/11-installazione.md per eseguirlo.";
        }
    }
}
