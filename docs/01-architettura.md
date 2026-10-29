# 1. Architettura — UPS Control Center

## 1.1 Principio guida

Il sistema ha **due piani di controllo indipendenti** con livelli di affidabilità diversi:

| Piano | Dove gira | Dipendenze | Ruolo |
|---|---|---|---|
| **Control Plane** (gestione) | Docker su `server-nut` | PostgreSQL, rete, Docker | Configurazione, UI, storico, simulazione, orchestrazione normale |
| **Safety Plane** (sicurezza) | systemd sull'host Ubuntu | solo NUT locale + SQLite locale | Garantisce lo spegnimento protettivo anche se Docker/PostgreSQL/rete sono giù |

Regola architetturale fondamentale: **il Safety Plane non deve mai dipendere dal Control Plane.**
Il Control Plane *configura* il Safety Plane, ma non è necessario al suo funzionamento.

## 1.2 Modalità operative

Stato globale persistito in `SystemSettings` + replicato nella config firmata del Safety Agent:

- `DRY_RUN` (**default, unico stato iniziale ammesso**): il motore decisionale gira completamente,
  valuta regole, costruisce ed "esegue" sequenze, registra ogni azione — ma gli adapter reali sono
  **non registrati nel container DI**. Al loro posto è registrato `DryRunDeviceAdapter`.
- `LIVE`: richiede (a) flag di sistema, (b) abilitazione per-adapter, (c) ruolo Administrator,
  (d) conferma esplicita con motivazione, (e) voce di audit. Non raggiungibile da un singolo pulsante.

L'isolamento DRY_RUN non è un `if` dentro l'adapter: è un **confine di composizione**. Gli adapter reali
vivono in assembly separati e vengono registrati solo da `AdapterRegistration` quando la modalità
effettiva lo consente. Un test dedicato verifica che in DRY_RUN nessun tipo reale sia risolvibile.

## 1.3 Flusso dati

```
NUT (host, 3493)
   │  protocollo testuale NUT (LIST VAR / GET VAR)
   ├────────────────────────────► ups-orchestrator (container)  ──► PostgreSQL (misure, eventi)
   │                                      │                              │
   │                                      └── SignalR ──► ups-web ◄──────┘
   │                                                        │
   │                                                        └── UI (Bootstrap 5 / Tabulator / Chart.js)
   │
   └────────────────────────────► ups-safety-agent (systemd)  ──► SQLite /var/lib/ups-control-center/safety.db
```

La configurazione viaggia in una sola direzione:

```
UI → PostgreSQL (ShutdownPolicies/Rules/Sequences)
        │ validazione + versionamento + checksum HMAC
        ▼
   ConfigurationVersions (stato: Draft → Validated → Published)
        │ pull autenticato HTTPS  (l'agent chiama, non viene chiamato)
        ▼
   Safety Agent → scrittura atomica in SQLite → ACK ricezione → ACK attivazione
```

Il Safety Agent **non apre porte in ingresso**: fa polling verso `ups-web` (`GET /api/safety/config/current`).
Questo evita di esporre superficie d'attacco sul componente più privilegiato del sistema.

## 1.4 Coordinamento Orchestrator ↔ Safety Agent

Problema: entrambi leggono NUT e potrebbero entrambi decidere di spegnere.

Soluzione — **ownership con fencing token, non lock temporale**:

- Ogni blackout riceve un `BlackoutId` deterministico: `{upsId}:{utcTicks-inizio-arrotondato}`,
  così i due processi convergono sullo stesso identificativo osservando lo stesso evento fisico.
- L'Orchestrator, se vivo, è **owner** per default e scrive heartbeat in `WorkerHeartbeats`
  (PostgreSQL) **e** nella tabella `coordination` di SQLite via API.
- Il Safety Agent esegue **takeover** solo se: heartbeat orchestrator più vecchio di
  `TakeoverGraceSeconds` (default 90s) **e** condizione di regola soddisfatta **e** nessuna azione
  risulta già completata per quel `(BlackoutId, StepId)`.
- Ogni azione è registrata con chiave univoca `(BlackoutId, SequenceRunId, StepId, DeviceId)`:
  l'idempotenza è garantita dal **vincolo di unicità nel database**, non dalla logica applicativa.
  Una seconda esecuzione viola il vincolo e viene scartata.
- Il takeover incrementa `FencingToken`. Azioni presentate con token inferiore a quello corrente
  sono rifiutate: se l'Orchestrator "resuscita" dopo un takeover, non può riprendere il comando
  senza una riconciliazione esplicita.

## 1.5 Perché non microservizi

`ups-notifications` e `ups-simulator` sono **moduli** dentro i processi esistenti, non container
separati, perché condividono il motore decisionale e il modello dati e non hanno profili di scala
indipendenti. Il compose li espone come servizi logici opzionali (profili Docker Compose) per
rispettare il requisito §19 senza moltiplicare processi su un mini PC a risorse limitate.
Eccezione: `ups-simulator` può essere avviato come container separato per isolare carichi di
simulazione pesanti, usando la stessa immagine dell'orchestrator con un entrypoint diverso.
