# 7. Piano Safety Agent

## 7.1 Identità del componente

- Progetto: `UPSControlCenter.SafetyAgent` (.NET 10 Worker Service)
- Unit systemd: `ups-safety-agent.service`, `Type=notify`, `WatchdogSec=60`
- Utente: `upsagent` (creato dallo script di installazione, shell `/usr/sbin/nologin`)
- Nessuna dipendenza da: Docker, PostgreSQL, SignalR, portale web, rete aziendale
- Dipendenze effettive: `upsd` su `127.0.0.1:3493`, SQLite locale

## 7.2 Macchina a stati

```
                 ┌──────────────┐
      start ────►│ Initializing │ carica config attiva da SQLite, legge boot_id
                 └──────┬───────┘
                        │ config valida?
          ┌─────────────┴─────────────┐
       no │                           │ sì
          ▼                           ▼
   ┌─────────────┐             ┌──────────────┐
   │ SafeHold    │             │ Reconciling  │ riconcilia blackout/run aperti
   │ monitor+log │             └──────┬───────┘
   │ no azioni   │                    ▼
   └─────────────┘             ┌──────────────┐  OL stabile
                               │ Monitoring   │◄──────────────┐
                               └──────┬───────┘               │
                                      │ OB rilevato           │
                                      ▼                       │
                               ┌──────────────┐  OL ritorna   │
                               │ BlackoutOpen │───────────────┘
                               │ timer attivo │
                               └──────┬───────┘
                                      │ soglia raggiunta AND orchestrator assente
                                      ▼
                               ┌──────────────┐
                               │ Executing    │ sequenza, idempotente, fencing
                               └──────┬───────┘
                                      ▼
                               ┌──────────────┐
                               │ PostShutdown │ attesa ritorno corrente / fine vita
                               └──────────────┘
```

`SafeHold` è lo stato di sicurezza quando la configurazione è assente, corrotta o con firma non
valida: **l'agent continua a monitorare e a registrare eventi, ma non esegue nessuna azione** e
segnala la condizione come `Critical`. Questa è la scelta documentata richiesta da §13: in assenza
di una configurazione fidata, eseguire spegnimenti sarebbe più pericoloso del non agire, perché
l'ordine delle dipendenze non è conoscibile.

## 7.3 Loop principale

1. **Poll NUT** (`polling_interval`, default 10s): `LIST VAR riello`. Timeout 5s.
2. **Normalizza** in `UpsSnapshot`: flag OL/OB/LB/CHRG/DISCHRG/ALARM/RB/BYPASS, `battery.charge`,
   `battery.runtime`, `ups.load`, tensioni, frequenze, dati trifase. Variabili assenti → `null`.
3. **Debounce**: una transizione di stato è accettata solo dopo `debounce_samples` letture
   concordi consecutive (default 3 ⇒ 30s). Protegge da micro-interruzioni e letture spurie.
   Eccezione: `LB` e `battery critica` hanno `debounce_samples = 1` perché sono già condizioni terminali.
4. **Freschezza**: se l'ultima lettura valida è più vecchia di `stale_data_threshold`, lo stato
   diventa `Stale` → trattato come condizione critica con policy dedicata, **non** come OL.
5. **Timer monotono**: aggiorna la durata del blackout con `Stopwatch`, persiste ogni 30s in SQLite.
6. **Valuta regole** con `RuleEvaluator` — *lo stesso codice dell'orchestrator e del simulatore*.
7. **Verifica ownership**: se l'orchestrator ha heartbeat recente, l'agent non agisce e logga
   `DeferredToOrchestrator`. Altrimenti takeover con incremento del fencing token.
8. **Esegue** la sequenza tramite adapter: in DRY_RUN sempre e solo `DryRunDeviceAdapter`.
9. **Heartbeat** su `recovery_state.last_heartbeat_at` + `sd_notify(WATCHDOG=1)` a systemd.

## 7.4 Sincronizzazione configurazione

Pull ogni 60s (o on-demand al riavvio) su `GET /api/safety/config/current`:

```
agent ──► web:  GET /api/safety/config/current   (mTLS o bearer token locale)
web   ──► agent: { version, payload, checksumSha256, signature }
agent: verifica HMAC-SHA256(payload, SAFETY_CONFIG_HMAC_KEY) == signature
       verifica checksum, verifica version > versione attiva
       verifica schema + regole di coerenza (DAG aciclico, soglie plausibili)
       ┌ tutto ok → TX: INSERT Received → UPDATE Active, precedente Superseded → ACK
       └ errore   → INSERT Rejected(reason) + critical_event, config attiva INVARIATA
agent ──► web: POST /api/safety/config/{version}/ack       (ricezione)
agent ──► web: POST /api/safety/config/{version}/activated  (attivazione)
```

Se `ups-web`/PostgreSQL non rispondono: nessun errore fatale, l'agent prosegue con la
configurazione attiva locale. Un'indisponibilità prolungata genera un `critical_event`
`ConfigSyncStale` ma **non** cambia comportamento (§3.3).

Rollback: il Control Plane pubblica una versione precedente con un numero di versione **nuovo e
superiore** (non si "torna indietro" nel numero), contenente il payload storico. Così la regola
`version >` resta sempre valida e il percorso di rollback è identico a quello di aggiornamento.

## 7.5 Limite fisico e strategia di riaccensione

Il Safety Agent non può operare con il mini PC spento. Conseguenze di progetto:

- Lo step "arresto mini PC NUT" è **l'ultimo possibile** di ogni sequenza, disabilitato per default,
  e ammesso solo dopo la verifica che tutti gli step successivi siano vuoti.
- Il mini PC va mantenuto alimentato dall'UPS finché serve controllo. Con 10 kVA e carico ridotto
  dopo gli spegnimenti, l'autonomia residua del solo mini PC è ampia.
- Per la riaccensione dopo blackout totale la catena non può dipendere dall'agent: serve
  **BIOS "Restore on AC Power Loss = Power On"** sul mini PC e sugli switch/apparati di rete
  necessari a raggiungere i dispositivi (§12). Documentato in `docs/09-prerequisiti.md`.
- Al riavvio l'agent entra in `Reconciling`: rilegge lo stato, confronta `boot_id`, decide se il
  blackout è concluso e se esistono run interrotti da chiudere come `Aborted` con motivo
  `AgentRestarted`.

## 7.6 Installazione

`deploy/scripts/install-safety-agent.sh` — **mostra i comandi e chiede conferma prima di
modificare l'host** (§25.20). Operazioni: creazione utente, directory `/var/lib` e `/opt`,
copia binari self-contained, installazione unit, `daemon-reload`, `enable`, `start`.
Non tocca `nut-*`, non tocca PeaNUT, non modifica UFW senza conferma separata.
