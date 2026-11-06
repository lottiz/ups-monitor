# Stato del lavoro — UPS Control Center

Ultimo aggiornamento: 2026-11-06

## Obiettivo corrente

Stabilizzazione del primo deploy reale e rifinitura dell'interfaccia dopo la Fase 3. Il sistema
resta obbligatoriamente in `DRY_RUN`. La Fase 3 è completata, con un solo gap di verifica aperto
(vedi §"Rischi aperti" sotto: adapter WinRM non validato contro un host reale).

## Stato

| Elemento | Stato |
|---|---|
| Fase 1 (fondamenta) | completata, vedi `docs/01`–`docs/12` |
| Fase 2 (sequenze, editor, esecuzione) | completata, vedi `docs/13-fase2.md` |
| Nuovo progetto `UPSControlCenter.Adapters.Live` | SSH (Linux+QNAP), Redfish, Wake-on-LAN, WinRM — referenziato solo da Web/Orchestrator, mai dal Safety Agent |
| `CompositeDeviceAdapter` | dispatch per-protocollo (shutdown/startup/check indipendenti), abilitazione LIVE letta a ogni chiamata |
| `ExecutionModeSyncService` | risincronizza `ExecutionModeOptions` con `SystemSettings` ogni 10s, senza riavvio |
| Endpoint `POST /api/admin/live/enable-mode` | nuovo: prima non esisteva alcun modo di attivare `ExecutionMode=Live` |
| Pinning fingerprint/thumbprint | SSH (host key) e Redfish/WinRM (certificato TLS): nessuna azione reale senza valore pinnato; solo "Test connessione" fa trust-on-first-use |
| Test automatici | **219 totali**: 206 passano, 13 skip ambientali (PostgreSQL non disponibile in questo ambiente), 0 warning |
| Pagina Report (`/Reports`) | nuova: tabella storica (Tabulator) di carico, autonomia residua e altre metriche per UPS, periodo a date libere e intervallo di campionamento in minuti; esportazione CSV. Riusa `MeasurementSeriesSampler` già esistente per i grafici dashboard. Il periodo (giorni di calendario nel fuso `Europe/Rome`) è convertito in UTC da `DisplayTimeFormatter.ToUtcStartOfDay`, nuovo metodo coerente col fix fuso orario di d93aa41 |
| Media carico/autonomia nel report | `GET /Reports/Summary` calcola `AVG` lato SQL su carico e autonomia residua sulle letture grezze del periodo (non sul campione mostrato in tabella, che falserebbe la media tenendo solo l'ultima lettura di ogni finestra); i valori assenti sono ignorati da `AVG`, non trattati come zero (§9.3) |
| Verifica end-to-end reale | catena di attivazione LIVE completa (sincronizzazione, endpoint, kill switch) contro portale e PostgreSQL reali; ogni adapter contro sessioni/server programmabili o realmente in ascolto (Kestrel HTTPS con certificato autofirmato per Redfish, socket UDP reale per Wake-on-LAN) |
| TLS del portale | corretto il sito Caddy da `:443` a `{$BIND_ADDRESS}:443`: la CA interna emette ora il certificato per l'IP LAN esplicito; Compose passa `BIND_ADDRESS` al proxy |
| Interfaccia portale | restyling responsive “control room”: navigazione, dashboard, card UPS, metriche, tabelle e form; gli indicatori SignalR riflettono lo stato reale e non sono solo decorativi |
| Orari interfaccia | conversione esplicita da UTC a `Europe/Rome`, coerente tra rendering Razor e aggiornamenti JavaScript; ora solare/legale applicata automaticamente |
| Dashboard energetica | trend responsive per autonomia, batteria/carico e tensioni; distribuzione infrastruttura; serie browser campionate ogni 20 minuti senza rallentare il polling NUT |
| Grafico "Autonomia e carico" | la card dashboard ex "Batteria e carico" mostra ora autonomia (minuti, asse sinistro) e carico (%, asse destro) su doppio asse, per leggere quanta autonomia resta a un dato carico |
| Corrente in ampere (`output.current`/`input.current`) | nuovi campi nullable su `UpsSnapshot`/`UpsMeasurement`/`UpsStatusDto` (migrazione `AddUpsCurrentAmps`), popolati solo se il driver NUT li espone — mai stimati lato server (§9.3) |
| Pagina Analisi carico (`/Ups/LoadAnalysis/{id}`) | lettura live della corrente (reale se disponibile, altrimenti stimata client-side da potenza÷tensione ed esplicitamente etichettata "stimata") via SignalR esistente; registro di letture annotate manualmente (localStorage, per UPS) con colonna differenza, per isolare l'assorbimento di un dispositivo scollegandolo e confrontando la lettura prima/dopo; esportazione CSV. Linkata dalla pagina Dettagli UPS |
| Corrente nei dati storici | `OutputCurrentAmps` aggiunto a `UpsMeasurementHistoryDto` (DTO condiviso da grafici dashboard e Report): colonna "Corrente" e media nel Report, nuova card grafico "Corrente" nella Dashboard (doppio asse A/% nel grafico del Report insieme al carico) |
| Corrente/potenza su driver trifase senza totale | il Riello installato (driver `riello_ser`?) espone solo `output.L1/L2/L3.current` e `.realpower`, mai i totali `output.current`/`ups.realpower`: `NutTcpClient.BuildSnapshot` ora fa fallback sulla somma delle fasi presenti (`SumPhaseValues`) quando il totale manca — aggregazione di dati reali, non una stima (§9.3). Senza questo fix la pagina Analisi carico mostrava sempre "non disponibile" |
| **Nota per il deploy** | `ups-web` NON applica le migrazioni da solo: va sempre eseguito `docker compose run --rm ups-migrate` dopo un rebuild che include modifiche allo schema (es. migrazione `AddUpsCurrentAmps`), altrimenti il portale va in errore generico perché interroga colonne che non esistono ancora |

## Restyling interfaccia

Rinnovati layout condiviso e dashboard senza cambiare controller, DTO o flussi operativi. La
barra di navigazione evidenzia la sezione corrente; modalità `DRY_RUN`/LIVE, stato UPS e stato
della connessione in tempo reale restano sempre testuali oltre che cromatici. Tutti gli asset
rimangono locali e compatibili con la CSP esistente; nessuna dipendenza web o font esterno.

Verifiche: build completa **0 warning / 0 errori**; test .NET **217 passati, 0 saltati**
con PostgreSQL 16 portabile; `git diff --check` pulito.

Correzione successiva alla verifica visiva: SignalR serializza `ExecutionMode` come numero;
la dashboard mostrava quindi `0`. La UI mappa ora esplicitamente `0`/`DryRun` a `DRY_RUN` e
`1`/`Live` a `LIVE`; un valore sconosciuto resta “non determinata” e non viene mai scambiato
per LIVE.

Gli orari non usano più `ToLocalTime()`, che nel container restituiva UTC. Un formatter condiviso
converte esplicitamente gli istanti nel fuso `Display:TimeZone` (configurato da `TZ`, default
`Europe/Rome`); lo stesso fuso è esposto agli script client per dashboard, audit, configurazioni e
storico UPS. L'immagine Web installa i dati IANA `tzdata`. I test coprono UTC+1 in inverno e UTC+2
in estate. Verificato anche l'avvio dell'app pubblicata: `/health/live` 200, `/` 302 e
`/api/dashboard/automation` 401 senza autenticazione.

Il cruscotto include ora quattro grafici responsive (autonomia, batteria/carico, tensioni e stato
infrastruttura), card metriche cromatiche e tabella eventi collassabile sui display stretti. Le
misure continuano a essere registrate a ogni polling NUT per mantenere freschi stato e decisioni;
l'API grafici conserva l'ultima lettura di ogni finestra di 20 minuti, riducendo una giornata a un
massimo di 72 punti per UPS. Test automatici dedicati verificano campionamento e ordinamento.

## Ultima correzione di deploy — TLS su IP LAN

Al primo avvio reale Caddy partiva e generava la CA, ma il sito senza host esplicito non aveva
un subject per cui emettere il certificato quando il client raggiungeva direttamente
`10.0.0.8`. Inoltre `auto_https off` disabilitava la gestione/caricamento del certificato anche
con `tls internal`, lasciando l'handshake in errore: il blocco globale è stato rimosso. Il parser
ufficiale Caddy conferma ora `listen: :443`, matcher host `10.0.0.8` e policy di automazione TLS
con subject `10.0.0.8`. Aggiornati anche i manuali: i probe esterni usano HTTPS e il Safety Agent
deve puntare all'IP presente nel certificato, dopo aver installato la CA Caddy nel trust store
dell'host.

Una seconda verifica sul server con Caddy 2.11.9 ha mostrato il certificato emesso correttamente
ma ancora `internal error` da `curl`: i client che si collegano direttamente a un IPv4 possono
omettere SNI. Riprodotto con `openssl s_client -noservername` su Caddy 2.11.8; aggiunto
`default_sni {$BIND_ADDRESS}` perché Caddy selezioni il certificato IP anche senza SNI.

Verifiche: `deploy/scripts/test-install-all.sh` **38 passati**; test .NET **217 passati,
0 saltati** con PostgreSQL 16 portabile; `caddy adapt` riuscito con `BIND_ADDRESS=10.0.0.8`.

Dettaglio completo in `docs/14-fase3.md` (architettura, pinning, difetti trovati, tabella).

## Decisioni prese in questa fase

1. **`IDeviceAdapter.SupportedShutdownProtocol`/`SupportedStartupProtocol`** come proprietà con
   default `null`: un dispositivo ha protocolli di spegnimento e accensione indipendenti (es.
   SSH per spegnere, Wake-on-LAN per accendere).
2. **Un solo campo (`ConnectionProfile.Fingerprint`) per due usi**: fingerprint SSH e thumbprint
   TLS, con la stessa regola di pinning in entrambi i casi.
3. **WinRM scritto a mano su `HttpClient`**, non su una libreria di terze parti: non esiste un
   client WinRM .NET multipiattaforma maturo. Conseguenza accettata: è il solo adapter di questa
   fase non validato contro un host reale (nessun target Windows disponibile qui).
4. **`ExecutionModeOptions` è un singleton mutabile, risincronizzato da un `BackgroundService`**,
   non un valore immutabile letto una sola volta all'avvio: altrimenti un'abilitazione decisa dal
   portale non avrebbe mai effetto senza riavviare il processo.
5. **Comando fisso per profilo**, mai configurabile né derivato da input utente, per SSH e WinRM:
   nessun canale di shell remota generico, coerente con `docs/08` §8.3.

## Rischi aperti più rilevanti

- **Adapter WinRM non validato contro un host Windows reale** (nessun target disponibile in
  questo ambiente di sviluppo). La logica di decisione è pienamente testata contro una sessione
  programmabile; il protocollo WS-Management hand-rolled no. **Prima di abilitare `WinRm` in
  `LiveEnabledAdapters` su un deployment reale, verificare end-to-end.**
- QNAP via API nativa (`QnapApi`) non implementato: solo `QnapSsh` (adapter SSH, comando
  `poweroff`) disponibile.
- R4, R19 (parzialmente chiuso: SSH/Redfish/WinRM/WoL ora esistono, ma senza verifica contro
  dispositivi fisici) — vedi `docs/10`.
- Risorse reali del mini PC non note — `docs/09` §9.1.

## Verifiche non ancora eseguite

- `docker compose build`/`up`: Docker non è presente in questo ambiente di sviluppo.
- Lettura da un UPS reale, SSH/Redfish/WinRM/BMC reali: nessun target raggiungibile qui.
- Un blackout reale end-to-end con UPS e dispositivi fisici.

## Da fare — prossime fasi

1. Validare l'adapter WinRM contro un host Windows reale con WinRM/HTTPS configurato.
2. Retry manuale di una sequenza fallita, dal portale (gap ereditato dalla Fase 2).
3. QNAP via API nativa, se necessario oltre a `QnapSsh`.
4. Un UI/flusso per raccogliere e salvare il fingerprint/thumbprint osservato da "Test
   connessione" (oggi il risultato è solo nel messaggio di risposta, non c'è ancora un pulsante
   "salva questo valore" nel portale).

## Prossimo passo consigliato

**Profilo consigliato: Medium.** Il lavoro restante (punto 2 e 3 sopra) è implementazione
isolata su pattern già stabiliti. Punto 1 (validazione WinRM reale) richiede un host Windows
disponibile, non un profilo di ragionamento più alto — è un gap di infrastruttura di test, non
di complessità del problema.

Un'escalation a High sarebbe giustificata solo se la validazione del punto 1 rivelasse un
disallineamento strutturale nel protocollo WS-Management hand-rolled (non solo un bug puntuale):
in quel caso riconsiderare se vale la pena continuare a scriverlo a mano o cercare un'alternativa.
