# UPS Control Center

Portale aziendale per il monitoraggio degli UPS tramite Network UPS Tools (NUT) e
l'orchestrazione controllata di spegnimento e riaccensione dell'infrastruttura IT.

> **Il sistema è in modalità `DRY_RUN` e non invia alcun comando reale di spegnimento,
> riavvio o accensione.** L'isolamento non è un controllo condizionale: in DRY_RUN gli
> adapter reali non vengono registrati nel contenitore di dipendenze, quindi non possono
> essere risolti né eseguiti. Il passaggio a `LIVE` richiede cinque condizioni concorrenti
> descritte in [docs/08-sicurezza.md](docs/08-sicurezza.md).

## Stato: Fase 1 completata

| Fase | Contenuto | Stato |
|---|---|---|
| **1** | Solution, PostgreSQL + EF Core, Identity, dashboard, lettura NUT, gestione UPS e dispositivi, Safety Agent con SQLite, health check, Docker Compose, documentazione | **completata** |
| **2** | `SequenceExecutor` condiviso, esecuzione reale delle sequenze (Orchestrator e Safety Agent), riaccensione automatica con approvazione manuale (casi B/C/D di §11), simulatore con esecuzione reale degli step, editor regole e sequenze, pagina Audit | **completata** — build e 153 test verdi |
| 3 | Adapter reali (SSH, WinRM, QNAP, Redfish, Wake-on-LAN), gestione credenziali, test non distruttivi | da fare |
| 4 | Retry manuale delle sequenze fallite, editor condizioni annidate, verifica su infrastruttura reale | da fare |
| 5 | Hardening, backup verificati, notifiche complete, test di resilienza, procedure LIVE | da fare |

## Architettura

Due piani di controllo indipendenti, con livelli di affidabilità diversi:

| Piano | Dove gira | Dipendenze | Ruolo |
|---|---|---|---|
| **Control Plane** | Docker su `server-nut` | PostgreSQL, rete, Docker | configurazione, interfaccia, storico, simulazione, orchestrazione ordinaria |
| **Safety Plane** | systemd sull'host | solo `upsd` su loopback + SQLite locale | protegge l'infrastruttura anche con Docker, PostgreSQL e rete aziendale indisponibili |

Il Safety Agent **non dipende** dal Control Plane: viene configurato da esso, ma funziona
senza di esso. È il vincolo architetturale centrale del progetto.

```
NUT (host, 3493)
   ├──► ups-orchestrator (container) ──► PostgreSQL ──► ups-web ──► browser
   └──► ups-safety-agent (systemd)   ──► SQLite /var/lib/ups-control-center/safety.db
```

Dettaglio completo: [docs/01-architettura.md](docs/01-architettura.md) e
[docs/02-componenti.md](docs/02-componenti.md).

## Documentazione

| Documento | Contenuto |
|---|---|
| [01-architettura.md](docs/01-architettura.md) | principi, modalità operative, coordinamento fra i due piani |
| [02-componenti.md](docs/02-componenti.md) | diagrammi fisico e logico, confini di rete |
| [03-solution.md](docs/03-solution.md) | struttura della solution e dipendenze consentite |
| [04-schema-postgresql.md](docs/04-schema-postgresql.md) | schema, indici, vincoli, retention |
| [05-schema-sqlite.md](docs/05-schema-sqlite.md) | schema del Safety Agent, pragma, tempo monotono |
| [06-docker-compose.md](docs/06-docker-compose.md) | servizi, reti, volumi, aggiornamento e rollback |
| [07-safety-agent.md](docs/07-safety-agent.md) | macchina a stati, sincronizzazione configurazione |
| [08-sicurezza.md](docs/08-sicurezza.md) | DRY_RUN, abilitazione LIVE, kill switch, RBAC |
| [09-prerequisiti.md](docs/09-prerequisiti.md) | verifiche obbligatorie sull'host, NUT, firewall |
| [10-rischi.md](docs/10-rischi.md) | 22 rischi con mitigazioni e rischi residui accettati |
| [11-installazione.md](docs/11-installazione.md) | manuale di installazione |
| [12-amministratore.md](docs/12-amministratore.md) | manuale dell'amministratore |
| [13-fase2.md](docs/13-fase2.md) | esecuzione delle sequenze, casi B/C/D, editor regole e sequenze, difetti trovati con la verifica reale |

## Requisiti

- Ubuntu Server con Docker Engine e Docker Compose
- NUT già installato e funzionante sull'host (`upsd` attivo, `nut-monitor` disabilitato)
- .NET SDK 10 per compilare il Safety Agent, che gira nativamente sull'host
- Risorse: **da verificare prima del deploy** con `nproc`, `free -h`, `df -h`
  (vedi [docs/09](docs/09-prerequisiti.md) §9.1 — il mini PC ospita già PeaNUT e NUT)

## Avvio rapido

```bash
# 1. Verificare i prerequisiti dell'host, senza modificare nulla
./deploy/scripts/install-safety-agent.sh          # anteprima: non applica niente

# 2. Configurare i segreti
cp .env.example .env && chmod 600 .env
openssl rand -base64 36   # POSTGRES_PASSWORD
openssl rand -base64 48   # SAFETY_CONFIG_HMAC_KEY
openssl rand -base64 32   # SAFETY_AGENT_TOKEN
# compilare .env, incluse le credenziali del primo amministratore

# 3. Build e migrazioni (job dedicato: le migrazioni non girano all'avvio dell'app)
docker compose build
docker compose --profile migrate run --rm ups-migrate

# 4. Avvio
docker compose up -d
curl -fsS http://127.0.0.1:8443/health/ready
```

Procedura completa, Safety Agent e firewall: [docs/11-installazione.md](docs/11-installazione.md).

## Compilazione e test

```bash
dotnet build UPSControlCenter.sln
dotnet test tests/UPSControlCenter.Tests
```

**130 test**, tutti verdi. 117 girano senza dipendenze esterne; 13 richiedono un PostgreSQL
e vengono **saltati** (non passati) se non è disponibile:

```bash
UPS_TEST_CONNECTION="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres"   dotnet test tests/UPSControlCenter.Tests
```

Copertura degli scenari di §21:

| Area | Cosa viene verificato |
|---|---|
| Blackout | 5 / 20 / 30 minuti, autonomia sotto soglia, LB immediato, ritorno corrente, secondo blackout |
| Valori mancanti | `battery.runtime` assente non diventa mai 0; logica a tre valori su dati ignoti; le tre `UnknownRuntimePolicy` |
| Tempo | tempo monotono, correzione NTP durante un blackout, riavvio con `boot_id` diverso, durata non ricostruibile, debounce |
| Protocollo NUT | dialogo reale contro un server simulato, errori `ERR`, connessione chiusa, server assente, 10 letture consecutive |
| DAG | cicli diretti e indiretti, ordinamento topologico, simmetria spegnimento/accensione, determinismo |
| Sicurezza | **in DRY_RUN nessun adapter reale è registrato né risolvibile**; LIVE senza `UPS_ALLOW_LIVE` ricade su DRY_RUN; kill switch prevalente |
| Vincoli strutturali | nessun comando NUT di controllo nel codice; nessun `Process.Start`; il Safety Agent non dipende da Npgsql |
| Integrità config | checksum e firma HMAC, payload alterato, chiave errata, validazione (LIVE senza adapter, cicli, step `ShutdownNutHost` non finale) |
| SQLite | idempotenza, mutua esclusione, fencing token, recupero dopo riavvio, backup `VACUUM INTO` |
| PostgreSQL | gli stessi vincoli verificati **nel database reale**: unicità per idempotenza, indici parziali, check constraint, `NULL` preservati |

## Interventi sull'host

Nessuno script modifica l'host senza mostrare prima i comandi e chiedere conferma.
Il sistema **non** modifica NUT, **non** modifica PeaNUT (porta 8085), **non** installa
un secondo NUT e **non** esegue mai `FSD`, `killpower` o `upsdrvctl shutdown`: l'assenza
di questi comandi è verificata da test automatici sul codice sorgente.

## Emergenza

```bash
# Disabilita tutte le automazioni mantenendo attivo il monitoraggio.
# Funziona anche con portale e database indisponibili.
sudo ./deploy/scripts/disable-automation.sh "motivazione"
sudo ./deploy/scripts/disable-automation.sh --status
```

## Infrastruttura di riferimento

- Mini PC: `server-nut`, `10.0.0.8`, Ubuntu Server
- UPS: Riello/RPS S3M 10 CPT S1 (`DS3MK10CS100RUA`), 10 kVA, USB FTDI, driver `riello_ser`
- NUT: `riello@10.0.0.8:3493`
- PeaNUT esistente su `:8085` — non viene toccato
- Reti autorizzate: `10.0.0.0/24`, `172.16.10.0/24`, `192.168.98.0/24`, `192.168.114.0/24`
