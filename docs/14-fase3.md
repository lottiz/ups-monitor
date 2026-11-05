# 14. Fase 3 — Adapter reali, credenziali, test non distruttivi

Questo documento riassume ciò che la Fase 3 aggiunge alle Fasi 1 e 2. Per l'architettura di
base, lo schema del database e i principi di sicurezza, vedere i documenti `01`–`13`: restano
validi e non sono ripetuti qui.

## 14.1 Nuovo progetto `UPSControlCenter.Adapters.Live`

Separato da `UPSControlCenter.Adapters` (che resta DryRun-only) perché referenziato **solo** da
Web e Orchestrator: il Safety Agent non lo referenzia e resta quindi, anche in Fase 3,
programmaticamente privo di qualunque percorso verso operazioni reali (§13, invariante che non
cambia). Contiene cinque adapter — SSH (Linux e QNAP), Redfish, Wake-on-LAN, WinRM — più il
dispatcher e il servizio di sincronizzazione descritti sotto.

## 14.2 Protocolli indipendenti per spegnimento e accensione

`IDeviceAdapter` guadagna due proprietà con implementazione di default (`null`):
`SupportedShutdownProtocol` e `SupportedStartupProtocol`. Un dispositivo può spegnersi via SSH e
accendersi via Wake-on-LAN: sono due preoccupazioni indipendenti, quindi il dispatch deve
avvenire per singola azione, non per adapter-per-dispositivo. `CompositeDeviceAdapter` risolve
`ShutdownAsync`/`StartupAsync` confrontando questi campi con `ManagedDevice.ShutdownProtocol`/
`StartupProtocol`, e `CheckStateAsync` separatamente in base a `StatusCheckMethod` — tre assi
indipendenti.

L'abilitazione LIVE (`ExecutionModeOptions.LiveEnabledAdapters`) è letta **a ogni chiamata**, non
catturata alla costruzione del dispatcher: un'abilitazione decisa dall'amministratore ha effetto
senza riavviare il processo. Se il protocollo richiesto non ha un adapter reale abilitato, l'
azione ricade sul simulato invece di eseguire un'operazione reale non autorizzata — non è un
errore, è la granularità per-protocollo richiesta dal progetto.

## 14.3 Fingerprint/thumbprint: lo stesso campo, due significati

`ConnectionProfile.Fingerprint` è riusato sia per il fingerprint SHA-256 della chiave host SSH
sia per il thumbprint SHA-256 del certificato TLS (Redfish, WinRM). In entrambi i casi vale la
stessa regola, applicazione diretta del divieto di `StrictHostKeyChecking=no`:

- **Le azioni reali** (Shutdown, Startup, CheckState) si rifiutano di procedere se il campo è
  vuoto, con un codice d'errore dedicato (`SSH_FINGERPRINT_NOT_PINNED`,
  `REDFISH_CERT_NOT_PINNED`, `WINRM_CERT_NOT_PINNED`) — e senza aprire alcuna connessione.
- **Solo `TestConnectionAsync`** implementa trust-on-first-use: se non pinnato, accetta la
  connessione per questa sola volta al solo scopo di osservare e riportare il valore
  all'amministratore, che deve ancora salvarlo esplicitamente prima che qualunque azione reale
  possa usarlo.
- Se un valore è già pinnato, anche `TestConnectionAsync` lo verifica rigorosamente: la modalità
  permissiva è riservata esclusivamente all'host mai visto prima.

## 14.4 SSH (Linux, QNAP)

`SshDeviceAdapter` usa SSH.NET dietro un'astrazione testabile (`ISshSessionFactory`/
`ISshSession`), perché in questo ambiente di sviluppo non è disponibile un server SSH reale
contro cui validare (nessun accesso root per installarne uno). Due profili distinti
(`SshShutdownProfile.Linux` → `sudo /sbin/shutdown -h now`, `.Qnap` → `poweroff`) condividono lo
stesso adapter: un solo comando fisso per profilo, mai derivato da input dell'utente — nessun
canale di shell generico.

Non supporta l'accensione (`SSH_CANNOT_START` sempre): SSH presuppone un sistema operativo già
attivo. La disconnessione durante l'esecuzione del comando di spegnimento è trattata come
successo (`SSH_SHUTDOWN_DISCONNECTED`), non come errore: è l'esito atteso quando il comando ha
davvero avviato l'arresto.

## 14.5 Redfish (BMC / XClarity)

`RedfishDeviceAdapter` implementa lo standard DMTF: `ComputerSystem.Reset` con
`ResetType: GracefulShutdown` per lo spegnimento e `On` per l'accensione — **mai** `ForceOff`,
non esiste alcun percorso di codice che lo invochi. Lo stato (`PowerState`, lettura via
`GET /redfish/v1/Systems/1`) è interpretato con `TryGetProperty` case-sensitive in PascalCase,
la convenzione reale dello standard, non quella camelCase di default di ASP.NET Core (rilevante
solo per il server di test fittizio, non per la produzione: vedi §14.8).

Il pinning del certificato TLS usa `HttpClientHandler.ServerCertificateCustomValidationCallback`,
confrontando `Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256))` con il
valore registrato — stesso principio del fingerprint SSH, implementazione specifica a HTTPS.

## 14.6 Wake-on-LAN

`WakeOnLanStartupAdapter` costruisce il magic packet (6 byte `0xFF` + 16 ripetizioni del MAC, 102
byte) e lo invia via UDP broadcast sulla porta 9, dietro `IMagicPacketSender` per la
testabilità. Non supporta lo spegnimento (`WOL_CANNOT_SHUTDOWN` sempre) né la verifica di stato
(`WOL_NO_STATE_CHECK`, defer a `StatusCheckMethod`): un magic packet è un invio best-effort senza
conferma di consegna, lo stato risultante (`DeviceState.Starting`) lo dichiara esplicitamente.

## 14.7 WinRM (server Windows)

`WinRmShutdownAdapter` segue lo stesso schema di SSH — comando fisso (`shutdown /s /f /t 0`),
nessuna accensione (`WINRM_CANNOT_START`: WinRM presuppone Windows già in esecuzione), pinning
obbligatorio del thumbprint TLS — ma la sessione reale (`WinRmHttpSessionFactory`) non si
appoggia a una libreria di terze parti: non esiste un client WinRM .NET multipiattaforma
maturo e mantenuto equivalente a SSH.NET. L'implementazione scrive direttamente le busta SOAP
WS-Management (Create/Command/Receive/Signal/Delete sulla risorsa shell `cmd`) sopra
`HttpClient`, seguendo la struttura pubblica della specifica MS-WSMV.

**Non è stata validata contro un host Windows reale**, perché non ne esiste uno disponibile in
questo ambiente di sviluppo. La logica di decisione dell'adapter (gating delle credenziali,
pinning del certificato, comando fisso, interpretazione degli esiti) è invece pienamente
testata contro una sessione WinRM programmabile (`FakeWinRmSessionFactory`), esattamente come
per SSH. **Prima di abilitare `WinRm` in `LiveEnabledAdapters` su un deployment reale, verificare
end-to-end contro un host Windows con WinRM via HTTPS configurato** — è l'unico adapter di
questa fase per cui questo passaggio non è già stato fatto.

## 14.8 Difetti trovati con la verifica end-to-end reale

| Difetto | Causa | Correzione |
|---|---|---|
| `ICredentialProtector` non risolvibile all'avvio di Web | Registrato `AddScoped`, ma i nuovi adapter Fase 3 (singleton) ne dipendono: mismatch di lifetime rilevato solo all'avvio | Cambiato in `AddSingleton` |
| Orchestrator non poteva decifrare le credenziali dei dispositivi | `AddDataProtection()`/`ICredentialProtector` non erano mai stati registrati in Orchestrator (non necessario prima della Fase 3) | Aggiunti, condividendo lo stesso percorso/volume delle chiavi di Web |
| Un'abilitazione LIVE decisa dal portale non aveva mai effetto sul processo in esecuzione | `ExecutionModeOptions` è un singleton letto una sola volta all'avvio dalla sola configurazione statica, mai risincronizzato con `SystemSettings` | Aggiunto `ExecutionModeSyncService` (poll ogni 10s), che aggiorna `Mode`/`LiveEnabledAdapters` sul singleton esistente senza mai toccare `DeploymentAllowsLive` |
| Nessun modo per attivare la modalità LIVE | Esistevano l'abilitazione per-adapter e il reset al kill switch, ma nessun endpoint impostava mai `ExecutionMode = Live` | Aggiunto `POST /api/admin/live/enable-mode`, con conferma testuale esatta `"LIVE"`, motivazione obbligatoria e gli stessi controlli (`UPS_ALLOW_LIVE`, kill switch) |
| Nel server di test Redfish, mutazioni e letture del test non raggiungevano mai il server realmente in esecuzione | `FakeRedfishServer.StartAsync()` costruiva e restituiva una **seconda** istanza diversa da quella su cui si chiudevano gli handler delle route Kestrel | Reso `Port` mutabile internamente e restituita la stessa istanza (`state`) usata dagli handler |
| Due test `RedfishDeviceAdapterTests` sulla lettura di `PowerState` fallivano sempre con `DeviceState.Unknown` | `Results.Json(new { PowerState = ... })` nel server di test applicava la naming policy camelCase di default di ASP.NET Core (`powerState` sul filo), mentre l'adapter cerca correttamente `PowerState` in PascalCase (la vera convenzione DMTF) | Corretto il server di test per scrivere il corpo JSON a mano con il nome esatto — il bug era nel test, non nell'adapter |
| `ExecutionModeSyncServiceTests` non vedeva mai i dati seminati in un `DbContext` InMemory diverso | Il nome del database InMemory era generato con `Guid.NewGuid()` **dentro** la lambda di configurazione, rieseguita a ogni creazione di contesto: ogni scope otteneva un database isolato | Generato il nome una sola volta, fuori dalla lambda |

## 14.9 Cosa resta per le fasi successive

- Validazione end-to-end dell'adapter WinRM contro un host Windows reale (§14.7): è il solo
  gap di verifica rimasto di questa fase.
- Verifica end-to-end con dispositivi SSH, Redfish e Wake-on-LAN realmente raggiungibili: non
  eseguibile in questo ambiente di sviluppo (nessun server SSH installabile, nessun BMC/host
  reale). Verificata invece in modo completo: la logica di decisione di ogni adapter contro
  sessioni/server programmabili (inclusi un server Kestrel HTTPS con certificato autofirmato
  reale per Redfish e un vero socket UDP in ascolto per Wake-on-LAN), e l'intera catena di
  attivazione LIVE (sincronizzazione runtime, endpoint di abilitazione, kill switch) contro un
  portale e un PostgreSQL realmente in esecuzione.
- QNAP via API nativa (`QnapApi` in `ShutdownProtocol`) resta non implementato: solo `QnapSsh`
  (stesso adapter SSH, comando `poweroff`) è disponibile in questa fase.
