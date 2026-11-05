# Stato del lavoro — UPS Control Center

Ultimo aggiornamento: 2026-11-05

## Obiettivo corrente

Fase 2 del piano di sviluppo (§24): regole, sequenze, simulazione, esecuzione. **Completata.**

## Stato

| Elemento | Stato |
|---|---|
| Fase 1 (fondamenta) | completata, vedi commit precedente e `docs/01`–`docs/12` |
| `SequenceExecutor` condiviso (Core) | scritto, testato (17 test dedicati), nessuno stato mutabile |
| Esecuzione reale nell'Orchestrator | `ShutdownExecutionCoordinator` + `StartupExecutionCoordinator`, in background, non bloccano il polling |
| Esecuzione reale nel Safety Agent | sostituito lo stub di Fase 1, usa lo stesso `SequenceExecutor` |
| Simulatore | esegue realmente gli step (non solo il marcatore "avviata"), stesso motore |
| Casi B/C/D di §11 | implementati: B (policy-dipendente), C (stabilità + approvazione manuale), D (sempre interrotto) |
| Editor regole | `RulesController` + builder visuale AND/OR + modalità avanzata JSON |
| Editor sequenze | `SequencesController`, unico per spegnimento/riaccensione, con verifica dipendenze |
| Pagina Audit | aggiunta, sola lettura |
| Migrazione `AddStartupApproval` | applicata su PostgreSQL reale |
| Test automatici | **153, tutti verdi** (una flakiness ambientale nota, non di prodotto, vedi sotto) |
| Verifica end-to-end reale | eseguita: login, creazione regole/sequenze, salvataggio step, verifica dipendenze, audit — tutto contro un'istanza vera dell'app e un PostgreSQL vero |

## Decisioni prese in questa fase

1. **`SequenceExecutor` nel Core, senza stato mutabile.** Può essere un singleton condiviso da
   esecuzioni concorrenti. Non dipende da `Microsoft.Extensions.Logging`: la diagnostica passa
   per un callback `Action<EventSeverity,string>?` — il `Core` resta senza dipendenze esterne
   (vincolo documentato in `docs/03`).
2. **Attesa iniettabile** (`Func<TimeSpan, CancellationToken, Task>? delay`). Necessario perché
   il Simulatore deve eseguire in millisecondi una sequenza che in produzione prevede minuti di
   ritardo: senza questo parametro, condividere il motore con il Simulatore sarebbe stato
   impossibile senza riscriverlo.
3. **Esecuzione sempre in background, mai inline nel ciclo di polling/monitoraggio.** Vale per
   Orchestrator e, soprattutto, per il Safety Agent: bloccare il suo loop per la durata di una
   sequenza lo priverebbe della capacità di osservare l'UPS esattamente quando serve di più.
4. **Un solo tentativo automatico di spegnimento per blackout.** Se fallisce, non si ritenta
   automaticamente (verificato con una query su *tutti* gli stati, non solo quelli attivi). Un
   retry automatico senza supervisione è stato giudicato un rischio da non introdurre senza
   revisione dedicata.
5. **Caso D sempre attivo, indipendente dalla policy.** A differenza del caso B (scelta
   dell'operatore), continuare ad accendere dispositivi quando la corrente è di nuovo assente
   non è mai la scelta giusta: non è configurabile.
6. **Approvazione manuale della riaccensione come campo persistito, non come chiamata diretta.**
   Web e Orchestrator sono processi distinti che comunicano solo tramite PostgreSQL;
   `OrchestrationRun.ApprovedAt`/`ApprovedBy` è il canale, rilevato dall'Orchestrator al ciclo
   di polling successivo.
7. **Un solo controller per le sequenze di spegnimento e riaccensione**, parametrizzato su
   `SequenceKind`: `ShutdownStep`/`StartupStep` sono identici dal punto di vista dell'editor.
8. **Estratto `UPSControlCenter.Adapters`** da `Infrastructure`: il Safety Agent aveva bisogno
   di `IAdapterSelector`/`DryRunDeviceAdapter` ma non può referenziare `Infrastructure` (porta
   Npgsql). Nessuno dei due file aveva in realtà bisogno di Npgsql/EF Core: erano solo nella
   cartella sbagliata fin dalla Fase 1.
9. **Builder di regole a un solo livello (AND/OR su foglie), con modalità avanzata JSON come
   ripiego** per alberi più complessi (NOT, nidificazioni). Un editor visuale completamente
   recursivo è stato giudicato fuori scope per questa fase.

## Difetti trovati con la verifica end-to-end reale (non rilevabili da build/test unitari)

| Difetto | Causa | Correzione | Come l'ho trovato |
|---|---|---|---|
| L'app non partiva all'avvio (`AggregateException` sulla composizione DI) | `AddDbContext` + `AddDbContextFactory` per lo stesso `UpsDbContext`: due registrazioni di `DbContextOptions<T>` con lifetime incompatibili | Un solo punto di registrazione (`AddDbContextFactory`), `UpsDbContext` scoped derivato dalla factory | Avviando il binario pubblicato |
| Salvataggio di una regola falliva silenziosamente ("Aggiungere almeno una condizione") anche con condizioni compilate | `IReadOnlyList<T>` su proprietà di form MVC: il model binder non istanzia la collezione per campi indicizzati (`SimpleConditions[0].Condition`) | Cambiate in `List<T>`. Aggiunto test di architettura `ModelBindingSafetyTests` con controllo negativo eseguito | Login reale + compilazione form + ispezione risposta HTML |

Entrambi erano invisibili a `dotnet build` e `dotnet test`: sono emersi solo avviando
l'applicazione pubblicata con una sessione autenticata vera. **Lezione da applicare alle fasi
successive: continuare a fare questa verifica dopo ogni modifica a DI o a form con collezioni.**

## Verifiche eseguite in questa sessione

- Build Debug e Release, zero errori, zero warning nuovi.
- 153 test automatici verdi (117+36 nuovi/estesi in Fase 2).
- Migrazione `AddStartupApproval` applicata a un PostgreSQL reale (binario embedded, non il
  sistema): colonne verificate nello schema generato.
- Avvio reale di tutti e tre i processi (Web, Orchestrator, SafetyAgent) con le nuove
  registrazioni DI: nessun errore di composizione.
- Login reale, creazione di una sequenza e di uno step via form HTTP, salvataggio di una
  regola con builder di condizioni, verifica dipendenze, consultazione dell'audit — tutto
  verificato con richieste HTTP dirette contro un'istanza in esecuzione, non solo con i test.
- Controllo negativo sul nuovo test di regressione (`ModelBindingSafetyTests`): reintrodotta la
  violazione, il test l'ha rilevata; corretta di nuovo.

## Flakiness risolta (era un bug nel test, non ambientale)

`NutProtocolIntegrationTests.Letture_consecutive_funzionano_senza_perdere_la_connessione`
falliva in modo intermittente sotto l'esecuzione parallela della suite. Prima ipotesi
(sbagliata): contesa di porte TCP ambientale, non di prodotto. Indagando l'errore effettivo
(`Expected: 10, Actual: 9` sul conteggio dei `LOGOUT` ricevuti dal server) la causa reale era
una **race condition nel test stesso**: il client chiude il socket subito dopo aver scritto
`LOGOUT`, senza attendere l'`OK Goodbye` del server; l'elaborazione lato server (che registra
il comando) è quindi asincrona rispetto al ritorno di `ReadAsync`. L'asserzione leggeva
`server.ReceivedCommands` immediatamente dopo il decimo ciclo, prima che il server avesse
necessariamente finito di processare l'ultima connessione. Corretto attendendo con un breve
timeout (5s, poll ogni 20ms) che il conteggio raggiunga 10, invece di assumerlo immediato.
Verificato con 6 esecuzioni consecutive dell'intera suite, tutte stabili.

## Rischi aperti più rilevanti (invariati da Fase 1, vedi `docs/10`)

- **R4**: `battery.runtime` potrebbe sovrastimare. Non eliminabile da software.
- **R19**: capability Wake-on-LAN/QNAP/Redfish non verificate — nessun adapter reale esiste
  ancora (Fase 3).
- Risorse reali del mini PC non note: `docs/09` §9.1 elenca le verifiche da fare prima del deploy.

## Verifiche non ancora eseguite

- `docker compose build`/`up`: Docker non è presente in questo ambiente di sviluppo.
- Lettura da un UPS reale: NUT non è presente qui. Il protocollo è verificato end-to-end contro
  un server simulato (`FakeNutServer`), l'esecuzione delle sequenze contro adapter scriptati.
- Installazione del Safety Agent su host reale (`install-safety-agent.sh --apply`).
- Un blackout reale end-to-end con UPS e dispositivi fisici (non riproducibile in questo
  ambiente): la fiducia qui viene dalla composizione di parti verificate singolarmente
  (protocollo NUT, motore di regole, esecutore di sequenze, vincoli di database), non da un
  test end-to-end fisico.

## Da fare — Fase 3 (adapter reali)

1. Adapter SSH (Linux), WinRM (Windows), Redfish (Lenovo XClarity), QNAP, Wake-on-LAN.
   `AdapterRegistration.AddDeviceAdapters` è pronto ad accoglierli: oggi, anche in modalità LIVE,
   non registra nulla oltre al simulato.
2. Gestione credenziali: cifratura già pronta (`ICredentialProtector`/Data Protection), manca
   l'uso effettivo per connettersi con SSH/WinRM.
3. Test di connessione non distruttivi realmente funzionanti (oggi il pulsante "Test" nella UI
   chiama sempre l'adapter simulato, perché nessun adapter reale esiste).
4. Retry manuale di una sequenza fallita, dal portale (oggi non esiste: un fallimento richiede
   intervento diretto sul database o attende un nuovo blackout).

## Prossimo passo consigliato

Fase 3, iniziando dall'**adapter SSH** (il più semplice e il più utile per i server Linux già
modellati): è lo stesso pattern per tutti gli altri (implementare `IDeviceAdapter`, registrarlo
in `AdapterRegistration` dietro `LiveEnabledAdapters`, mai attivo senza le cinque condizioni di
`docs/08` §8.2).

**Profilo consigliato: Medium.** Ogni adapter è un'implementazione isolata di un'interfaccia già
definita e testata (`IDeviceAdapter`), con un protocollo documentato (SSH, WinRM, Redfish sono
tutti standard con librerie .NET esistenti). Non richiede decisioni architetturali nuove.

Un'escalation a High sarebbe giustificata solo per una decisione specifica non ancora presa:
**come gestire il fingerprint/host-key verification per SSH** in un ambiente dove le chiavi
host dei server non sono ancora state raccolte — bilanciare sicurezza (niente
`StrictHostKeyChecking=no`, già vietato in `docs/08`) e usabilità del primo setup.
