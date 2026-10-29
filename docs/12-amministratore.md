# 12. Manuale dell'amministratore

## 12.1 Operazioni quotidiane

### Dashboard

La prima cosa visibile è il **banner della modalità**. Confondere DRY_RUN con LIVE è il
rischio operativo più grave del sistema, quindi la modalità è ripetuta anche nel badge
della barra di navigazione, su ogni pagina.

| Banner | Significato |
|---|---|
| **DRY_RUN** (azzurro) | regole valutate, sequenze registrate, **nessun comando inviato** |
| **LIVE** (giallo) | operazioni reali possibili sugli adapter elencati |
| **Automazioni disabilitate** (rosso) | kill switch attivo: monitoraggio sì, azioni no |

### Lettura dei dati UPS

I campi riportano **"non disponibile"** quando il driver NUT non fornisce la variabile.
Non vengono mai mostrati valori calcolati o di ripiego: se un campo è vuoto, quel dato
non esiste e nessuna regola può basarsi su di esso.

Un riquadro **"Dato obsoleto"** indica che l'ultima misura è più vecchia della soglia
configurata. In quel caso i valori mostrati erano validi quando sono stati letti, ma non
sono verificabili come attuali — e il motore decisionale li tratta come inutilizzabili.

### Stato dei due piani

| Indicatore | Interpretazione |
|---|---|
| Orchestratore: `Running` | il Control Plane monitora NUT e scrive lo storico |
| Orchestratore: `Non risponde da Ns` | il Safety Agent è candidato al takeover dopo 90s |
| Safety Agent: `Monitoring` | configurazione attiva, in attesa di eventi |
| Safety Agent: `SafeHold` | **nessuna configurazione fidata**: non eseguirà azioni |
| Safety Agent: `BlackoutOpen` | blackout in corso, timer attivo |
| Safety Agent: `Mai visto` | agent non installato o non raggiungibile |

`SafeHold` non è un guasto transitorio da ignorare: significa che l'infrastruttura non è
protetta dal Safety Plane. Va risolto pubblicando una configurazione valida.

## 12.2 Gestione dei dispositivi

Campi che meritano attenzione:

- **Priorità** — valori più bassi vengono spenti prima. Non sostituisce le dipendenze:
  queste hanno sempre la precedenza nell'ordinamento.
- **UPS di alimentazione** — si possono selezionare più UPS. Il dispositivo è considerato
  privo di alimentazione solo quando **tutte** le sorgenti sono in blackout. Un dispositivo
  senza alcun UPS associato viene evidenziato in rosso nell'elenco: non è protetto.
- **Metodo di verifica stato** — `Ping` è il più debole: un ping senza risposta **non è
  prova di arresto**. Per i server critici usare `TcpPort`, `Ssh` o `Redfish`.
- **Indirizzo MAC** — il supporto Wake-on-LAN non è presunto. Va verificato sul dispositivo
  e nel BIOS; molte schede lo perdono dopo una perdita totale di alimentazione.

### Test di connessione

Il pulsante **Test** è non distruttivo. In DRY_RUN **non apre alcuna connessione** e lo
dichiara nel messaggio: serve a verificare la configurazione, non la raggiungibilità. Per
quest'ultima occorre abilitare l'adapter corrispondente in LIVE.

## 12.3 Regole di spegnimento

La policy predefinita implementa la regola **30 + 30**: la sequenza parte alla **prima**
fra queste due condizioni.

1. Blackout continuo di almeno 30 minuti.
2. Autonomia residua stimata ≤ 30 minuti durante lo stato `OB`.

Più lo stato `LB`, trattato separatamente come condizione terminale.

### Politiche sui casi incerti

Sono la parte più importante della configurazione, perché determinano il comportamento
quando i dati non sono affidabili.

| Politica | Valore predefinito | Effetto |
|---|---|---|
| `UnknownRuntimePolicy` | `TreatAsInsufficient` | autonomia non disponibile ⇒ si spegne. Conservativo: `null` non viene letto né come 0 né come infinito |
| `UncertainDurationPolicy` | `TreatAsImmediateThreshold` | durata non ricostruibile dopo un riavvio ⇒ soglia considerata raggiunta |
| `CommunicationLostPolicy` | `HoldAndAlert` | NUT irraggiungibile ⇒ si segnala, non si agisce |
| `PowerRestoredDuringShutdownPolicy` | `CompleteThenRestart` | corrente che torna a sequenza avviata ⇒ si completa, poi si riparte |

`TreatAsImmediateThreshold` è deliberatamente aggressivo: nel dubbio, aver già spento in
modo ordinato è meno dannoso che restare acceso con una batteria di carica ignota. Se
nell'ambiente specifico questa scelta non è accettabile, usare `RequireManualDecision`,
accettando che in quel caso nessuna protezione automatica interviene.

## 12.4 Simulatore

Il simulatore usa **lo stesso motore decisionale** dell'esecuzione reale: non esiste un
percorso alternativo per le simulazioni. Una simulazione è quindi una previsione attendibile
della decisione che verrebbe presa, non un'approssimazione.

Scenari predefiniti disponibili: blackout di 5/20/30 minuti, autonomia sotto soglia,
autonomia non disponibile, LB improvviso, perdita di comunicazione, ritorno della corrente,
secondo blackout, crash dell'orchestrator.

La timeline è esportabile in CSV e ogni esecuzione viene archiviata: serve a documentare
le verifiche svolte prima di qualunque passaggio a LIVE.

**Da eseguire sempre dopo ogni modifica alle regole**, prima di pubblicare.

## 12.5 Pubblicazione della configurazione

```
Configurazioni → Anteprima → (verificare errori e avvisi) → Pubblica
```

Il flusso è: validazione → versione monotona → checksum SHA-256 → firma HMAC →
pubblicazione. L'agent scarica entro 60 secondi, verifica, valida di nuovo in autonomia,
e attiva in una singola transazione.

Se la validazione o la verifica falliscono, **l'agent mantiene la configurazione
precedente** e registra il rifiuto. Una configurazione non valida non sostituisce mai una
valida, e non viene mai applicata parzialmente.

Le colonne **Confermata** e **Attivata** nell'elenco versioni mostrano il percorso completo.
Se restano «in attesa» per più di qualche minuto, l'agent non sta raggiungendo il portale:
verificare il token e l'URL in `/etc/ups-control-center/safety-agent.env`.

### Rollback

Il rollback **ripubblica il payload di una versione precedente con un numero nuovo e
superiore**. L'agent accetta solo versioni crescenti, quindi il percorso di rollback è
identico a quello di aggiornamento. Richiede una motivazione, che finisce nell'audit.

## 12.6 Kill switch

Da portale: `POST /api/admin/automation/disable` (ruolo Operator o Administrator).

Da host, **funziona anche con portale e database indisponibili**:

```bash
sudo ./deploy/scripts/disable-automation.sh "motivo dell'intervento"
sudo ./deploy/scripts/disable-automation.sh --status
sudo ./deploy/scripts/disable-automation.sh --enable "motivo"
```

Lo script scrive un file sentinella locale che il Safety Agent controlla a ogni ciclo,
**prima** di qualunque valutazione. È il meccanismo di emergenza primario proprio perché
non dipende da nessun altro componente.

Rimuovere il blocco **non** riattiva la modalità LIVE: resta DRY_RUN.

## 12.7 Passaggio a LIVE

Non esiste un pulsante che attivi le operazioni reali. Servono **cinque condizioni
concorrenti**:

1. `UPS_ALLOW_LIVE=1` nell'ambiente del servizio, con riavvio del container.
2. `ExecutionMode = Live` nelle impostazioni (solo Administrator).
3. L'adapter specifico in `LiveEnabledAdapters`, abilitato singolarmente.
4. Conferma digitata: il nome dell'adapter, più una motivazione testuale.
5. Kill switch non attivo.

La condizione 1 esiste per un motivo preciso: se un dump del database proveniente da un
altro ambiente dichiarasse LIVE, senza quella variabile la modalità effettiva resterebbe
DRY_RUN e il tentativo verrebbe registrato come rifiutato (R18).

### Procedura consigliata

1. Completare le Fasi 3 e 4 (gli adapter reali non esistono ancora).
2. Verificare ogni dispositivo con il test di connessione, adapter per adapter.
3. Eseguire tutti gli scenari del simulatore e archiviare i CSV.
4. Abilitare **un solo adapter** su **un solo dispositivo non critico**.
5. Verificare l'esito sull'audit prima di estendere.

## 12.8 Ruoli

| Capability | Viewer | Operator | Administrator |
|---|---|---|---|
| Dashboard, storico, eventi | ✔ | ✔ | ✔ |
| Simulazioni | — | ✔ | ✔ |
| CRUD dispositivi e gruppi | — | ✔ | ✔ |
| Test di connessione | — | ✔ | ✔ |
| Modifica regole e sequenze | — | ✔ | ✔ |
| Kill switch | — | ✔ | ✔ |
| Pubblicazione configurazione | — | — | ✔ |
| Gestione credenziali | — | — | ✔ |
| Abilitazione LIVE | — | — | ✔ |
| Utenti e impostazioni | — | — | ✔ |

Il kill switch è volutamente accessibile a Operator: richiedere un amministratore durante
un incidente sarebbe un ostacolo nel momento peggiore.

## 12.9 Manutenzione

### Retention

| Dato | Predefinito | Impostazione |
|---|---|---|
| Misure UPS | 90 giorni | `MeasurementRetentionDays` |
| Eventi | 365 giorni | `EventRetentionDays` |
| Audit | 1825 giorni | `AuditRetentionDays` (non cancellato automaticamente) |

Crescita misurata: 1 UPS a 10 s ≈ 8.640 righe/giorno ≈ 2,6 milioni/anno. Con retention a
90 giorni lo spazio resta nell'ordine delle centinaia di MB.

### Aggiornamento

```bash
./deploy/scripts/backup-postgres.sh
sudo ./deploy/scripts/backup-sqlite.sh
# annotare l'IMAGE_TAG corrente per il rollback

docker compose build --pull
docker compose --profile migrate run --rm ups-migrate
docker compose up -d
curl -fsS http://127.0.0.1:8443/health/ready
```

Il Safety Agent continua a proteggere l'infrastruttura durante tutto l'aggiornamento del
Control Plane, con la sua ultima configurazione valida.

### Rollback

```bash
docker compose down                        # senza -v: i volumi restano
# ripristinare IMAGE_TAG precedente in .env
docker compose up -d
# se lo schema è cambiato:
./deploy/scripts/restore-postgres.sh /var/backups/.../upscc-<data>.dump
```

### Verifica periodica dei backup

Un backup non verificato non è un backup. Almeno una volta al trimestre:

```bash
pg_restore --list /var/backups/ups-control-center/upscc-<data>.dump | head
sqlite3 /var/backups/ups-control-center/safety-<data>.db "PRAGMA integrity_check;"
```

Lo script di backup PostgreSQL esegue già una verifica di leggibilità a ogni esecuzione e
scrive su file temporaneo, rinominandolo solo a successo: nella directory non compaiono mai
backup incompleti che sembrerebbero validi.

## 12.10 Diagnostica

| Sintomo | Verifica |
|---|---|
| Nessuna misura UPS | `docker compose logs ups-orchestrator`; `upsc riello@127.0.0.1`; `NUT_HOST` corretto? |
| Dashboard con dati obsoleti | orchestrator attivo? `/health/ready` segnala `Degraded`? |
| Agent in `SafeHold` | configurazione pubblicata? token e chiave HMAC coincidono? |
| Agent `Mai visto` | `systemctl status ups-safety-agent`; `journalctl -u ups-safety-agent` |
| Configurazione «in attesa» | l'agent raggiunge `ControlPlaneBaseUrl`? token corretto? |
| Portale non raggiungibile | `BIND_ADDRESS`; regole `DOCKER-USER`; filtro IP applicativo |
| 403 dal portale | rete di origine fra quelle autorizzate? |

Variabili NUT grezze: **UPS → Dettagli → Carica variabili**. Mostra esattamente ciò che il
driver fornisce; i campi assenti lì non possono comparire altrove.

```bash
# Log strutturati
docker compose logs -f ups-orchestrator | jq -r '"\(.["@t"]) \(.["@l"]) \(.["@m"])"'
sudo journalctl -u ups-safety-agent -f --output=cat | jq -r '.["@m"]'

# Stato interno del Safety Agent
sudo sqlite3 /var/lib/ups-control-center/safety.db <<'SQL'
.mode line
SELECT version, state, activated_at FROM safety_config WHERE state='Active';
SELECT * FROM coordination;
SELECT blackout_id, started_at_utc, ended_at_utc, duration_certain FROM blackouts
  ORDER BY started_at_utc DESC LIMIT 5;
SELECT occurred_at, event_type, severity FROM critical_events
  ORDER BY id DESC LIMIT 20;
SQL
```

## 12.11 Limiti noti

- **Il Safety Agent non può operare con il mini PC spento.** Lo step di arresto dell'host
  NUT è ammesso solo come ultimo della sequenza e disabilitato per default. Per la
  riaccensione dopo un blackout totale servono BIOS con *Restore on AC Power Loss = Power On*
  sul mini PC **e** sugli apparati di rete: senza rete non esistono né Wake-on-LAN né Redfish.
- **La precisione dell'autonomia dipende dal firmware dell'UPS.** `battery.runtime` è una
  stima, non una garanzia: si mitiga con soglie conservative, non si elimina.
- **DRY_RUN non dimostra che LIVE funzionerà.** Dimostra che le decisioni sono corrette e
  che nessun comando è partito. La verifica degli adapter reali è una campagna a sé.
- **Nessuna ridondanza del nodo di sicurezza.** Un guasto hardware del mini PC lascia
  l'infrastruttura senza protezione centralizzata.
