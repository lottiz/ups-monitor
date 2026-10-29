# 10. Rischi e mitigazioni

Scala: **P** probabilità, **I** impatto (1 basso – 5 critico).

## 10.1 Rischi di sicurezza operativa (i più gravi)

| # | Rischio | P | I | Mitigazione |
|---|---|---|---|---|
| R1 | Spegnimento non voluto dell'infrastruttura per bug nel motore regole | 3 | 5 | DRY_RUN per costruzione (§8.1); adapter reali non registrati; abilitazione LIVE a 5 fattori; simulatore con lo stesso motore; 21 scenari di test |
| R2 | Orchestrator e Safety Agent spengono entrambi lo stesso dispositivo | 3 | 4 | `BlackoutId` deterministico; ownership con fencing token; vincoli UQ su `(run, step, device, attempt)` in **entrambi** i database: la seconda esecuzione viola il vincolo |
| R3 | Spegnimento a cascata per dipendenze errate (DB spento prima delle app) | 3 | 4 | DAG con ordinamento topologico; cicli rifiutati in validazione; `device_dependencies` tipizzate (NFS/SMB/DB); verifica di completamento per step |
| R4 | `battery.runtime` sovrastimato dal driver ⇒ spegnimento troppo tardi | 3 | 5 | Il runtime **non** è trattato come previsione garantita (§9.3): regola "30+30" scatta sul **primo** tra durata e autonomia; `LB` è condizione terminale separata con debounce 1; soglia di sicurezza aggiuntiva su `battery.charge` |
| R5 | Valore NUT assente interpretato come 0 ⇒ falso allarme critico | 3 | 5 | Tutte le metriche nullable end-to-end; `null` ≠ 0 per contratto; policy `UnknownRuntimePolicy` configurabile ed esplicita; test dedicato |
| R6 | Mini PC spento ⇒ nessun controllo residuo | 2 | 5 | Step di arresto mini PC ultimo e disabilitato per default; mini PC mantenuto alimentato; BIOS auto-power-on documentato (§7.5) |
| R7 | Cicli accensione/spegnimento su rete elettrica instabile | 3 | 4 | Stabilità su `stability_samples` letture (default 10 ⇒ 5 min), non su una singola lettura; Caso D §11 interrompe nuove accensioni; cooldown anti-flapping |
| R8 | Riaccensione impossibile: switch giù ⇒ niente WoL/Redfish | 3 | 4 | Apparati di rete sotto UPS e con auto-power-on; capability `Unknown` finché non verificata; la sequenza di ripristino fallisce in modo esplicito invece di appendersi |

## 10.2 Rischi tecnici

| # | Rischio | P | I | Mitigazione |
|---|---|---|---|---|
| R9 | Porta Docker pubblicata bypassa UFW ⇒ portale esposto | 4 | 4 | Binding su IP specifico + regole `DOCKER-USER` + filtro IP applicativo: tre barriere indipendenti (§9.4) |
| R10 | Durata blackout non ricostruibile dopo riavvio | 3 | 3 | Tempo monotono + `boot_id`; `duration_certain = 0`; policy conservativa configurata e documentata; incertezza registrata (§5.3) |
| R11 | Corruzione SQLite per perdita di alimentazione | 2 | 4 | WAL + `synchronous=FULL`; transazioni singole; stato `SafeHold` se la config è illeggibile; backup con `VACUUM INTO` |
| R12 | Perdita del volume Data Protection ⇒ credenziali illeggibili | 2 | 4 | Backup del volume come passo obbligatorio documentato; verifica periodica di ripristino; rotazione chiavi pianificata |
| R13 | Crescita incontrollata di `ups_measurements` ⇒ disco pieno sul mini PC | 3 | 3 | Retention 90 gg + aggregazione oraria + BRIN; monitoraggio spazio con health check che degrada a `Degraded` |
| R14 | Migrazione EF applicata da due istanze in parallelo | 2 | 3 | Migrazioni in job dedicato (`compose run ups-web migrate`), non all'avvio dell'app |
| R15 | Modifica a `upsd.conf` rompe NUT o PeaNUT | 2 | 4 | Default `NUT_ACCESS_MODE=viaAgent` (zero modifiche); se richiesta, solo `LISTEN` aggiuntiva, backup, `reload` e non `restart`, rollback documentato (§9.3) |
| R16 | Risorse del mini PC insufficienti ⇒ OOM con PeaNUT | 3 | 3 | Limiti di memoria per container; verifica preliminare obbligatoria (§9.1); profilo `simulation` non attivo per default |
| R17 | Configurazione applicata parzialmente al Safety Agent | 2 | 4 | Versione monotona + checksum + HMAC; applicazione in singola transazione; ACK di ricezione e di attivazione separati; config invariata se la validazione fallisce (§7.4) |
| R18 | Ripristino di un dump da ambiente diverso riattiva LIVE | 2 | 5 | `UPS_ALLOW_LIVE=1` richiesta a livello di deployment: la modalità LIVE nel database senza la variabile viene ignorata e loggata (§8.2) |

## 10.3 Rischi di progetto

| # | Rischio | P | I | Mitigazione |
|---|---|---|---|---|
| R19 | Capability hardware assunte e inesistenti (WoL, API QNAP, Redfish) | 4 | 3 | Nessuna API inventata; capability `Unknown` per default, confermate solo da test non distruttivo; Redfish/XCC su specifica ufficiale |
| R20 | Il sistema sembra funzionare in DRY_RUN ma fallisce in LIVE | 3 | 4 | Test di connessione non distruttivi per adapter; abilitazione LIVE graduale per singolo adapter e singolo dispositivo; prima attivazione su un dispositivo non critico |
| R21 | Teams: i webhook `Office 365 Connector` sono deprecati da Microsoft | 4 | 2 | Non si usa l'URL connector legacy: si usa Workflows/Power Automate con payload Adaptive Card, dietro un'interfaccia `INotificationSender` sostituibile |
| R22 | Interferenza con PeaNUT | 2 | 3 | Nessuna porta/nome/volume in comune; nessuna modifica a `/opt/nut-dashboard`; verifica post-deploy che 8085 risponda |

## 10.4 Rischi residui accettati

- **Il Safety Agent non protegge da un guasto hardware del mini PC stesso.** Non c'è ridondanza:
  un secondo nodo di sicurezza è fuori scope. Mitigazione parziale: BIOS `Restore on AC Power Loss`
  e la capacità dei dispositivi di proteggersi autonomamente (`upsmon` locale) come fallback
  indipendente, da valutare in una fase successiva.
- **La precisione dell'autonomia dipende dal firmware dell'UPS.** Si mitiga con soglie
  conservative, non si elimina.
- **DRY_RUN non prova che LIVE funzionerà.** Dimostra solo che le decisioni sono corrette e che
  nessun comando reale è partito. Il passaggio a LIVE richiede una campagna di verifica dedicata
  per adapter, prevista in Fase 5.
