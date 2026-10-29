# Istruzioni per gli agenti che lavorano su questo repository

## Vincolo assoluto

Il sistema deve restare in **`DRY_RUN`**. Non introdurre codice che invii comandi reali di
spegnimento, riavvio o accensione, né comandi di controllo UPS.

Questi divieti sono verificati da test automatici in
`tests/UPSControlCenter.Tests/Adapters/NoDangerousCommandsTests.cs`:

- al socket NUT vengono inviati solo `LIST`, `LOGOUT`, `USERNAME`, `PASSWORD`;
- nel codice non compaiono `upsdrvctl`, `upsmon`, `upscmd`, `/etc/nut`, `killpower`,
  `shutdown.return`, `load.off` (i commenti che documentano il divieto sono esclusi
  dall'analisi, il codice no);
- non esiste alcun `Process.Start`;
- `UPSControlCenter.SafetyAgent` non referenzia Npgsql né EF Core.

Se uno di questi test fallisce, **non indebolire il test**: correggere il codice.

## Confini architetturali da non violare

| Regola | Perché |
|---|---|
| `SafetyAgent` **non** può referenziare `Infrastructure` | porterebbe Npgsql e renderebbe il Safety Plane dipendente da PostgreSQL (`docs/01` §1.1) |
| `Core` non ha dipendenze esterne | il dominio deve restare testabile e condivisibile |
| Il client NUT vive in `UPSControlCenter.Nut` | serve a entrambi i piani senza trascinare la persistenza |
| Un valore NUT assente è `null`, **mai** `0` | "autonomia 0" e "autonomia sconosciuta" portano a decisioni opposte (`docs/10` R5) |
| Un ping fallito non è prova di arresto | `DeviceState.Unreachable`, non `Off` (`docs/09` §10) |
| L'isolamento DRY_RUN è un confine di composizione | gli adapter reali non vengono registrati nel container, non disabilitati con un `if` |
| Le migrazioni girano in un job dedicato | due istanze che migrano in parallelo producono una race (`docs/06` §6.9) |
| Lo schema usa snake_case | i filtri degli indici parziali e i check constraint sono scritti in snake_case |

## Prima di iniziare

```bash
git status && git log --oneline -10
cat MEMORY.md                      # stato del lavoro e prossimi passi
ls docs/                           # 01..12: architettura, schemi, sicurezza, manuali
```

Verificare se il lavoro è già stato fatto o iniziato da un altro agente prima di rifarlo.

## Ambiente di sviluppo

Questo container **non è** il mini PC di produzione: non ha Docker, NUT né PostgreSQL di
sistema. .NET 10 è installato localmente.

```bash
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

dotnet build UPSControlCenter.sln
dotnet test tests/UPSControlCenter.Tests
```

### PostgreSQL per i test di integrazione

I 13 test di `PostgresConstraintTests` verificano i vincoli nel database reale. Senza
PostgreSQL vengono **saltati**, non passati. Per eseguirli serve un'istanza; in questo
ambiente è stata usata una build portabile di PostgreSQL 16 da Maven Central
(`io.zonky.test.postgres`), avviata su `127.0.0.1:55432`.

```bash
export UPS_TEST_CONNECTION="Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Password=postgres"
dotnet test tests/UPSControlCenter.Tests
```

### Verifica di avvio reale

Build e test non intercettano tutto: un conflitto di routing MVC si manifesta solo
all'avvio. Dopo modifiche a controller o DI, avviare davvero l'applicazione.

```bash
dotnet publish src/UPSControlCenter.Web -c Release -o /tmp/pub-web
cd /tmp/pub-web
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Postgres="Host=127.0.0.1;Port=5432;Database=x;Username=x;Password=x" \
SafetySync__HmacKeyBase64="$(openssl rand -base64 48)" \
SafetyApi__AgentToken=test DataProtection__KeyRingPath=/tmp/keys-test \
ASPNETCORE_URLS=http://127.0.0.1:5199 \
  "$HOME/.dotnet/dotnet" UPSControlCenter.Web.dll
```

Atteso: `/health/live` → 200, `/` → 302, `/api/dashboard/automation` → 401.

Il Safety Agent si avvia senza PostgreSQL (usa solo SQLite): è il modo più rapido di
verificarlo end-to-end.

## Modifiche alle migrazioni

```bash
dotnet ef migrations add <Nome> \
  --project src/UPSControlCenter.Infrastructure \
  --startup-project src/UPSControlCenter.Infrastructure \
  --output-dir Persistence/Migrations

dotnet ef migrations script --idempotent \
  --project src/UPSControlCenter.Infrastructure \
  --startup-project src/UPSControlCenter.Infrastructure \
  --output deploy/sql/initial-schema.sql
```

**Controllare l'SQL generato**: i filtri degli indici parziali e i check constraint devono
usare nomi di colonna in snake_case non quotati. Se compaiono `"PascalCase"`, la convenzione
di naming non è applicata e lo schema fallirà su PostgreSQL.

## Interventi sull'host di produzione

Non modificare mai l'host senza mostrare prima i comandi e chiedere approvazione. Gli script
in `deploy/` seguono questo schema: anteprima per default, `--apply` con conferma digitata.

Non toccare: NUT (`/etc/nut`, `nut-*.service`), PeaNUT (porta 8085, `/opt/nut-dashboard`),
i container Docker preesistenti, le regole UFW relative alla 8085.

## Convenzioni di codice

- Interfaccia e documentazione in **italiano**; identificatori in inglese.
- I commenti spiegano **perché**, non cosa: le scelte non ovvie e i vincoli di sicurezza.
- Nessun segreto nel codice o nel repository; nessuna password nei log.
- Catch ampi solo nei loop di monitoraggio, con motivazione nel commento: quei loop non
  devono morire, perché la loro morte lascia l'infrastruttura senza protezione.
