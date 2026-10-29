# 3. Struttura della solution

```
ups-monitor/
├── UPSControlCenter.sln
├── Directory.Build.props              # TFM net10.0, nullable, warnaserror
├── Directory.Packages.props           # central package management, versioni fissate
├── .env.example
├── docker-compose.yml
├── docker-compose.override.yml.example
├── docs/                              # 01..10 documenti di progetto
├── deploy/
│   ├── systemd/ups-safety-agent.service
│   ├── scripts/install-safety-agent.sh
│   ├── scripts/backup-postgres.sh
│   ├── scripts/backup-sqlite.sh
│   ├── scripts/restore-postgres.sh
│   └── firewall/ufw-rules.sh
├── src/
│   ├── UPSControlCenter.Core/
│   │   ├── Entities/                  # entità di dominio (§16)
│   │   ├── Enums/
│   │   ├── Abstractions/              # IDeviceAdapter, INutClient, IClock, ...
│   │   ├── Nut/                        # NutStatus, parsing flag OL/OB/LB
│   │   ├── Rules/                      # RuleEvaluator, BlackoutTracker, condizioni
│   │   ├── Sequencing/                 # DAG, topological sort, SequencePlanner
│   │   └── Safety/                     # SafetyConfiguration, checksum, policy critiche
│   ├── UPSControlCenter.Contracts/     # DTO REST + payload config firmata
│   ├── UPSControlCenter.Nut/            # client NUT condiviso, solo BCL + astrazioni
│   │   ├── NutTcpClient.cs              # sola lettura: LIST VAR / LIST UPS / LOGOUT
│   │   └── NutClientOptions.cs
│   ├── UPSControlCenter.Infrastructure/
│   │   ├── Persistence/UpsDbContext.cs + Configurations/ + Migrations/
│   │   ├── Security/CredentialProtector.cs (ASP.NET DataProtection)
│   │   └── Adapters/DryRunDeviceAdapter.cs + AdapterRegistration.cs
│   ├── UPSControlCenter.Infrastructure.Sqlite/   # solo per SafetyAgent
│   │   ├── SafetyStore.cs
│   │   └── Schema.cs
│   ├── UPSControlCenter.Web/           # MVC + API + SignalR + Identity
│   │   ├── Controllers/ Views/ wwwroot/ Hubs/ Areas/Identity/
│   ├── UPSControlCenter.Orchestrator/  # Worker: NutPollingService, RuleEngineService
│   ├── UPSControlCenter.SafetyAgent/   # Worker: systemd, SQLite, NUT locale
│   ├── UPSControlCenter.Simulator/     # motore condiviso + adapter fittizi
│   └── UPSControlCenter.Notifications/ # SMTP / Webhook / Teams
└── tests/
    └── UPSControlCenter.Tests/         # xUnit: Rules, Sequencing, Safety, DryRun, Sqlite
```

## Dipendenze consentite (verificate da test di architettura)

| Progetto | Può referenziare |
|---|---|
| Core | *(niente)* |
| Contracts | Core |
| Nut | Core |
| Infrastructure | Core, Contracts, Nut |
| Infrastructure.Sqlite | Core, Contracts |
| Web | Core, Contracts, Nut, Infrastructure, Notifications, Simulator |
| Orchestrator | Core, Contracts, Nut, Infrastructure, Notifications |
| SafetyAgent | Core, Contracts, Nut, **Infrastructure.Sqlite** |
| Simulator | Core, Contracts |
| Notifications | Core, Contracts |
| Tests | tutti |

Vietato: `SafetyAgent → Infrastructure` (porterebbe Npgsql e violerebbe §2.2). Il client NUT vive
in un progetto a sé proprio perché serve a entrambi i piani e non può trascinare con sé la
persistenza PostgreSQL. Un test di architettura verifica che l'assembly del Safety Agent non
carichi Npgsql.
