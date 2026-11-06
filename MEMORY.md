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
| Test automatici | **199 passano, 13 skip** (ambientali, non di prodotto), 0 warning |
| Verifica end-to-end reale | catena di attivazione LIVE completa (sincronizzazione, endpoint, kill switch) contro portale e PostgreSQL reali; ogni adapter contro sessioni/server programmabili o realmente in ascolto (Kestrel HTTPS con certificato autofirmato per Redfish, socket UDP reale per Wake-on-LAN) |
| TLS del portale | corretto il sito Caddy da `:443` a `{$BIND_ADDRESS}:443`: la CA interna emette ora il certificato per l'IP LAN esplicito; Compose passa `BIND_ADDRESS` al proxy |
| Interfaccia portale | restyling responsive “control room”: navigazione, dashboard, card UPS, metriche, tabelle e form; gli indicatori SignalR riflettono lo stato reale e non sono solo decorativi |

## Restyling interfaccia

Rinnovati layout condiviso e dashboard senza cambiare controller, DTO o flussi operativi. La
barra di navigazione evidenzia la sezione corrente; modalità `DRY_RUN`/LIVE, stato UPS e stato
della connessione in tempo reale restano sempre testuali oltre che cromatici. Tutti gli asset
rimangono locali e compatibili con la CSP esistente; nessuna dipendenza web o font esterno.

Verifiche: build completa **0 warning / 0 errori**; test .NET **199 passati, 13 saltati**
(PostgreSQL non disponibile); `git diff --check` pulito.

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

Verifiche: `deploy/scripts/test-install-all.sh` **38 passati**; test .NET **199 passati,
13 saltati** (PostgreSQL non disponibile); `caddy adapt` riuscito con `BIND_ADDRESS=10.0.0.8`.

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
