# 2. Diagramma dei componenti

## 2.1 Vista fisica (mini PC `server-nut` / 10.0.0.8)

```
┌───────────────────────────────────────────────────────────────────────────────┐
│ UBUNTU HOST — server-nut (10.0.0.8)                                           │
│                                                                               │
│  ┌──────────────────────┐   USB FTDI    ┌─────────────────────────────────┐   │
│  │ nut-driver@riello    │◄──────────────│ UPS Riello S3M 10 CPT S1        │   │
│  │ (riello_ser)         │               │ DS3MK10CS100RUA                 │   │
│  └──────────┬───────────┘               └─────────────────────────────────┘   │
│             │                                                                 │
│  ┌──────────▼───────────┐                                                     │
│  │ nut-server (upsd)    │  127.0.0.1:3493 + 10.0.0.8:3493                     │
│  │ nut-monitor: OFF     │  ◄── NON MODIFICARE                                 │
│  └──────┬───────────┬───┘                                                     │
│         │           │                                                         │
│         │           │  ┌──────────────────────────────────────────────┐        │
│         │           └─►│ ups-safety-agent.service  (systemd, .NET)    │        │
│         │              │  user: upsagent  (non root)                  │        │
│         │              │  ┌────────────────────────────────────────┐  │        │
│         │              │  │ SQLite WAL                             │  │        │
│         │              │  │ /var/lib/ups-control-center/safety.db  │  │        │
│         │              │  └────────────────────────────────────────┘  │        │
│         │              │  pull config ──► https://10.0.0.8:8443       │        │
│         │              └──────────────────────────────────────────────┘        │
│         │                                                                     │
│  ═══════│═════════════════════ DOCKER ════════════════════════════════════     │
│         │                                                                     │
│  ┌──────▼───────────────┐      ┌──────────────────────┐                       │
│  │ ups-orchestrator     │      │ ups-web              │                       │
│  │ Worker Service       │─────►│ ASP.NET Core MVC     │ :8080 (proxy) / :8443 │
│  │ NUT poll / regole    │ Sig  │ Identity + SignalR   │                       │
│  │ sequenze / adapter   │ nalR │ REST API             │                       │
│  └──────┬───────────────┘      └──────┬───────────────┘                       │
│         │                             │                                       │
│         │      ┌──────────────────────▼───────────┐                           │
│         └─────►│ ups-db  PostgreSQL 17 (no port)  │  volume: ups-db-data      │
│                └──────────────────────────────────┘                           │
│                                                                               │
│  ┌──────────────────────┐   ┌──────────────────────┐                          │
│  │ ups-notifications    │   │ ups-simulator        │  (profilo opzionale)     │
│  └──────────────────────┘   └──────────────────────┘                          │
│                                                                               │
│  ─── intoccabili ───                                                          │
│  ┌──────────────────────┐                                                     │
│  │ nut-peanut :8085     │  /opt/nut-dashboard — NON MODIFICARE                │
│  └──────────────────────┘                                                     │
└───────────────────────────────────────────────────────────────────────────────┘
```

## 2.2 Vista logica dei progetti

```
                       ┌─────────────────────────┐
                       │ UPSControlCenter.Core   │  dominio puro, zero dipendenze
                       │ entità, regole, motore  │  decisionale, interfacce adapter
                       └────────────▲────────────┘
                                    │
          ┌─────────────────────────┼─────────────────────────┐
          │                         │                         │
┌─────────▼──────────┐   ┌──────────▼──────────┐   ┌──────────▼─────────┐
│ .Contracts         │   │ .Infrastructure     │   │ .Adapters.*        │
│ DTO API, config    │   │ EF Core, Npgsql,    │   │ SSH/WinRM/Redfish  │
│ firmata, enum      │   │ NUT client, crypto  │   │ QNAP/WoL  (Fase 3) │
└─────────┬──────────┘   └──────────┬──────────┘   └──────────┬─────────┘
          │                         │                         │
    ┌─────┴────────┬────────────────┼──────────────┬──────────┘
    │              │                │              │
┌───▼────┐  ┌──────▼──────┐  ┌──────▼──────┐  ┌───▼────────┐  ┌──────────────┐
│ .Web   │  │.Orchestrator│  │ .SafetyAgent│  │ .Simulator │  │.Notifications│
│ MVC/API│  │ Worker      │  │ Worker+SQLite│ │ adapter fake│ │ SMTP/Webhook │
└────────┘  └─────────────┘  └─────────────┘  └────────────┘  └──────────────┘
                                    │
                             ┌──────▼──────┐
                             │ .Tests      │  xUnit — 21 scenari §21
                             └─────────────┘
```

`.SafetyAgent` **non** referenzia `.Infrastructure` (che porta Npgsql): usa
`.Infrastructure.Sqlite`, un assembly separato con solo `Microsoft.Data.Sqlite`.
Questo rende impossibile per costruzione una dipendenza dell'agent da PostgreSQL.

## 2.3 Confini di rete

| Da | A | Porta | Protocollo | Note |
|---|---|---|---|---|
| Reti autorizzate | ups-web | 8443 | HTTPS | unica porta pubblicata verso la LAN |
| ups-orchestrator | host NUT | 3493 | NUT | via `host.docker.internal` / gateway bridge |
| ups-orchestrator | ups-db | 5432 | TCP | rete interna Docker, **mai** pubblicata |
| safety-agent | 127.0.0.1 NUT | 3493 | NUT | loopback |
| safety-agent | ups-web | 8443 | HTTPS | pull config, solo in uscita |
| Orchestrator/Agent | dispositivi gestiti | 22/5986/443/9 | SSH/WinRM/Redfish/WoL | **chiuso in DRY_RUN** |
