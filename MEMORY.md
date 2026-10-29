# Stato del lavoro — UPS Control Center

Ultimo aggiornamento: 2026-10-29

## Obiettivo corrente

Fase 1 del piano di sviluppo (§24): fondamenta del sistema. **Completata.**

## Stato

| Elemento | Stato |
|---|---|
| Documenti di progettazione (10 richiesti + 2 manuali) | `docs/01`…`docs/12` |
| Solution .NET 10, 10 progetti | compila in Debug e Release, zero warning |
| Schema PostgreSQL + migrazione EF | applicata su PostgreSQL reale, SQL in `deploy/sql/initial-schema.sql` |
| Schema SQLite Safety Agent | creato e verificato a runtime (WAL, indici univoci) |
| Test automatici | **130, tutti verdi** (117 senza dipendenze + 13 su PostgreSQL) |
| Docker Compose, Dockerfile, `.env.example` | scritti, **non ancora eseguiti** (Docker assente in questo ambiente) |
| systemd unit + script di deploy | scritti, sintassi verificata, **non eseguiti su host reale** |
| Portale web | avviato realmente: endpoint pubblici 200/302, protetti 401 |
| Safety Agent | avviato realmente: crea lo schema, entra in SafeHold, gestisce NUT assente |

## Decisioni architetturali prese

1. **Due piani indipendenti.** Il Safety Agent (systemd, SQLite) non dipende da Docker,
   PostgreSQL, rete aziendale né portale. Imposto per costruzione: non referenzia
   `Infrastructure`, verificato da test.
2. **Client NUT in un progetto a sé** (`UPSControlCenter.Nut`): serve a entrambi i piani
   senza trascinare la persistenza PostgreSQL nel Safety Agent.
3. **DRY_RUN come confine di composizione**, non come `if`: in DRY_RUN gli adapter reali non
   vengono registrati nel container DI. Più barriera `UPS_ALLOW_LIVE` a livello di
   deployment, perché un dump ripristinato da un altro ambiente non deve poter attivare LIVE.
4. **Idempotenza e mutua esclusione nel database**, non nel codice: indici univoci su
   `(run, step, device, attempt)` e indici parziali su stati attivi. Verificati su PostgreSQL
   reale, non solo in memoria.
5. **`BlackoutId` deterministico** (`{ups}:{bucket-60s}`): due osservatori dello stesso evento
   convergono sullo stesso identificativo senza coordinarsi.
6. **Tempo monotono + `boot_id`** per la durata del blackout: l'orologio di parete può essere
   corretto da NTP durante un blackout.
7. **Logica a tre valori** (Kleene) nelle condizioni: un dato mancante non è né vero né falso.
   Le tre `UnknownRuntimePolicy` rendono esplicita la scelta, con `TreatAsInsufficient` come
   predefinito conservativo.
8. **snake_case nello schema** (`EFCore.NamingConventions`): necessario perché i filtri degli
   indici parziali e i check constraint sono scritti in snake_case.
9. **`synchronous=FULL` su SQLite**: il caso d'uso è la perdita di alimentazione del mini PC.
10. **Notifiche e simulatore come moduli**, esposti come servizi Compose opzionali: su un mini
    PC a risorse limitate microservizi separati non porterebbero vantaggi.

## Problemi trovati e risolti durante lo sviluppo

| Problema | Risoluzione |
|---|---|
| Nessun .NET 10 nell'ambiente | installato in `~/.dotnet` (10.0.403), SDK di sistema intatto |
| Lo SQL generato aveva colonne `"PascalCase"` ma filtri snake_case: **sarebbe fallito su PostgreSQL** | aggiunta `UseSnakeCaseNamingConvention()` in tutti i punti di configurazione, migrazione rigenerata |
| Conflitto di routing MVC (`[HttpGet("...")]` su controller convenzionale): **l'app non partiva** | rimosso l'attributo, JS allineato a `/Simulator/GetPresets`. Trovato solo avviando davvero l'app |
| Due test di vincolo rilevavano i commenti che documentano i divieti | resi precisi: analisi XML del csproj, commenti esclusi dalla scansione, whitelist sui comandi inviati al socket. **Controllo negativo eseguito**: introdotta una violazione reale, i test l'hanno rilevata |
| Lo stack trace di "NUT non raggiungibile" veniva scritto a ogni ciclo di polling | stack solo alla prima occorrenza e al cambio di errore, ripetizioni a Debug, messaggio di ripristino |

## Da fare — Fase 2

1. **Editor visuale delle regole** (`docs/09` §9.2): UI sull'albero `ConditionNode`, che è già
   completo e testato. Serve solo il frontend più il CRUD.
2. **Editor visuale delle sequenze** (§10): UI sul `DependencyGraph`, già completo con
   ordinamento topologico e rilevamento cicli.
3. **`SequenceExecutor`** — il pezzo più grosso che manca. Oggi il Safety Agent registra gli
   step in modo idempotente ma non esiste un esecutore completo con dipendenze, parallelismo,
   retry, timeout e verifica del completamento. L'orchestrator **non esegue sequenze**: rileva
   la soglia e la registra nei log (vedi `NutPollingService.PollOnceAsync`).
4. **Gestione del ritorno della corrente** (§11, casi A-D): il caso A è implementato
   (annullamento del timer). I casi B, C, D hanno gli enum di policy ma non la logica.
5. Timeline del simulatore già presente; manca l'esecuzione simulata delle sequenze.

## Rischi aperti più rilevanti

- **R4**: `battery.runtime` del driver `riello_ser` potrebbe sovrastimare. Mitigato con doppia
  soglia e LB separato, non eliminato. Da verificare sul campo con una prova controllata.
- **R9**: le regole `DOCKER-USER` non sono persistenti di default. Lo script le mostra e
  ricorda `iptables-persistent`, ma va verificato dopo il primo riavvio del mini PC.
- **R19**: capability Wake-on-LAN, API QNAP e Redfish **non verificate**. Il campo
  `StartupCapabilityVerified` resta `null` e i dispositivi non vengono usati per l'accensione
  finché non è confermato.
- Risorse reali del mini PC **non note**: §9.1 elenca le verifiche da fare prima del deploy.

## Verifiche non ancora eseguite

- `docker compose build` e `up`: Docker non è presente in questo ambiente di sviluppo.
- Lettura da un UPS reale: NUT non è presente qui. Il protocollo è però verificato
  end-to-end contro un server NUT simulato (`FakeNutServer`).
- Installazione del Safety Agent su host reale (`install-safety-agent.sh --apply`).

## Prossimo passo consigliato

Fase 2, iniziando dal **`SequenceExecutor`**: è la dipendenza di tutto il resto (editor
sequenze, casi B-C-D del ritorno corrente, simulazione delle azioni).

**Profilo consigliato: Medium.** Il lavoro è ampio ma concettualmente definito: il DAG, il
motore di regole, la registrazione idempotente e i vincoli di database esistono già e sono
testati. Serve comporli, non progettare da zero.

Un'escalation a High è giustificata solo per una decisione specifica: la **semantica esatta
del caso B** di §11 (corrente che torna a sequenza avviata), dove interrompere a metà può
lasciare l'infrastruttura in uno stato misto peggiore sia del completamento sia
dell'annullamento. Gli enum di policy sono già definiti; va decisa la logica di transizione.
