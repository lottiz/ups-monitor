# 13. Fase 2 — Esecuzione delle sequenze e editor

Questo documento riassume ciò che la Fase 2 aggiunge alla Fase 1. Per l'architettura di base,
lo schema del database e i principi di sicurezza, vedere i documenti `01`–`12`: restano validi
e non sono ripetuti qui.

## 13.1 `SequenceExecutor`: il motore condiviso

Il pezzo centrale lasciato aperto dalla Fase 1 (`MEMORY.md` lo indicava come priorità) è
`UPSControlCenter.Core.Sequencing.SequenceExecutor`. Esegue una sequenza di
`ExecutableStep` — una rappresentazione neutra, indipendente da `ShutdownStep`/`StartupStep`,
prodotta da `SequenceStepResolver` risolvendo gruppi ed esclusioni.

Caratteristiche:

- **Raggruppamento per esecuzione parallela** (`ParallelGroup`), con ordine complessivo
  determinato dal più piccolo `OrderIndex` del gruppo.
- **Retry con backoff implicito** (nessuna attesa fissa, il retry riparte subito): ogni
  tentativo è registrato distintamente in `ISequenceRunStore`; solo l'ultimo tentativo per
  dispositivo determina se la sequenza prosegue.
- **Timeout per step**, tramite `CancellationTokenSource.CancelAfter`.
- **Verifica del completamento** non interpretativa: un dispositivo irraggiungibile alla
  verifica non contraddice un'azione riuscita (nota "verifica non conclusiva"), ma un dispositivo
  che risulta ancora nello stato opposto a quello atteso fa fallire l'azione ed è soggetto a
  retry (§10 del progetto originale).
- **Idempotenza tramite `ISequenceRunStore`**, non tramite logica applicativa: tre
  implementazioni — `PostgresSequenceRunStore` (Orchestrator, vincolo univoco su
  `OrchestrationAction`), `SqliteSequenceRunStore` (Safety Agent, claim a due fasi su
  `operations`), `InMemorySequenceRunStore` (Simulatore e test).
- **Nessuno stato mutabile nell'istanza**: può essere un singleton condiviso da esecuzioni
  concorrenti (Orchestrator e Simulatore, o due sequenze diverse) senza interferenze. Il
  `Core` non referenzia `Microsoft.Extensions.Logging`: la diagnostica passa per un callback
  `Action<EventSeverity,string>?` opzionale.
- **Attesa iniettabile** (`Func<TimeSpan, CancellationToken, Task>? delay`): di default tempo
  reale (`Task.Delay`), sostituita dal Simulatore con un'attesa nulla, perché una sequenza con
  minuti di ritardo configurato deve restituire un esito in millisecondi quando simulata.

## 13.2 Orchestrator: esecuzione reale in background

`ShutdownExecutionCoordinator` reclama l'esecuzione tramite il vincolo di unicità su
`(BlackoutId, RunKind)` già esistente in Fase 1, poi la esegue in un `Task` indipendente dal
ciclo di polling NUT: una sequenza può durare minuti, e bloccare il polling per tutto quel tempo
significherebbe smettere di osservare l'UPS esattamente quando conta di più.

**Un solo tentativo automatico per blackout**: se la sequenza fallisce, non viene ritentata
finché il blackout è lo stesso (verificato con una query che include tutti gli stati, non solo
quelli attivi). Un retry automatico di uno spegnimento senza supervisione non è stato
considerato un comportamento sicuro da introdurre senza revisione dedicata; un nuovo tentativo
manuale è una funzionalità futura.

`StartupExecutionCoordinator` implementa i casi C e D di §11:

- **Caso C** (corrente ripristinata dopo lo spegnimento): la riaccensione parte solo dopo
  `StabilitySamples` letture OL consecutive **e** `RequiredStablePowerSeconds` di stabilità
  osservata (calcolata da intervallo di polling × letture concordi, non dal solo orologio).
  Se `RequireManualApproval` è vero, la run resta `Pending` finché un amministratore non la
  approva dal portale.
- **Caso D** (corrente nuovamente assente durante la riaccensione): l'esecuzione si interrompe
  al confine fra step **a prescindere dalla policy configurata** — a differenza del caso B, qui
  non è una scelta dell'operatore: continuare ad accendere dispositivi senza corrente non è mai
  corretto. I dispositivi già accesi non vengono toccati.

**Approvazione manuale fra processi distinti.** Web e Orchestrator sono processi separati che
comunicano solo tramite PostgreSQL. L'approvazione non è quindi una chiamata diretta: il portale
imposta `OrchestrationRun.ApprovedAt`/`ApprovedBy` (migrazione `AddStartupApproval`),
l'Orchestrator lo rileva al ciclo di polling successivo e avvia l'esecuzione.

Il caso B (ritorno corrente durante lo spegnimento) è implementato nel motore stesso: il
coordinatore passa un `shouldStopAtBoundary` che, se la policy è `StopAtNextStepBoundary`,
restituisce vero quando `NutPollingService` ha segnalato il ritorno della corrente per quel
blackout. Il kill switch ha sempre precedenza, in entrambe le direzioni.

## 13.3 Safety Agent: stesso motore, non più uno stub

In Fase 1 `ExecuteStepsAsync` registrava ogni step come "riuscito" senza invocare alcun adapter.
In Fase 2 usa `SequenceExecutor` con `SqliteSequenceRunStore` e `SafetyExecutionMapper` (converte
`SafetyDevice`/`SafetyStep` — i modelli leggeri della configurazione firmata — in `ManagedDevice`
e `ExecutableStep` transitori, mai persistiti). L'esecuzione gira in un task indipendente dal
ciclo di monitoraggio, per lo stesso motivo dell'Orchestrator — a maggior ragione per l'agent,
che è la rete di sicurezza di ultima istanza.

Mancava anche `PowerRestoredDuringShutdownPolicy` in `SafetyThresholds`/`SafetyConfiguration`:
la Fase 1 l'aveva definita sulla policy ma non l'aveva propagata alla configurazione distribuita
all'agent. Corretto in `ConfigurationPublisher.BuildAsync` e nel mapper `ToPolicy`.

Il Safety Agent registra sempre e solo l'adapter simulato (`AddDeviceAdapters` con
`Mode = DryRun` fisso in `Program.cs`): non esiste ancora, né è previsto in questa fase, un
percorso che porti l'agent a eseguire operazioni reali.

## 13.4 Simulatore: esecuzione reale degli step, non solo il marcatore

`BlackoutSimulator.RunAsync` (prima `Run`, sincrono) ora, al momento dello scatto della soglia,
invoca `SequenceExecutor` con `SimulatedAdapterSelector` (restituisce sempre
`SimulatedDeviceAdapter`, mai un adapter reale, per costruzione) e `InMemorySequenceRunStore`,
con attesa nulla. Ogni azione prodotta diventa una riga di timeline con dispositivo ed esito,
non solo "Avvio sequenza di spegnimento". Il controller (`SimulatorController`) carica la
sequenza predefinita della policy e i dispositivi coinvolti dal database, esattamente come
farebbe l'esecuzione reale.

## 13.5 Editor regole e sequenze

**Regole** (`RulesController`): un builder visuale compone un albero AND/OR di primo livello su
foglie semplici (flag di stato, soglie numeriche, condizioni booleane). Alberi più complessi
(NOT, gruppi annidati) restano modificabili in una modalità avanzata con il JSON grezzo
dell'albero `ConditionNode` — compromesso scelto per restare nello scope della fase senza
costruire un editor recursivo completo.

**Sequenze** (`SequencesController`): un solo controller gestisce sia le sequenze di spegnimento
sia quelle di riaccensione tramite un parametro `kind`, perché `ShutdownStep` e `StartupStep`
sono strutturalmente identici dal punto di vista dell'editor (differiscono solo nell'entità EF
di destinazione). Un pulsante "Verifica dipendenze" richiama lo stesso `DependencyGraph`
dell'esecuzione reale, mostrando cicli o l'ordine di spegnimento risultante.

## 13.6 Difetti trovati con la verifica end-to-end reale

Nessuno di questi era visibile da build o test unitari: sono emersi solo avviando l'applicazione
pubblicata e usandola con una sessione autenticata reale.

| Difetto | Causa | Correzione |
|---|---|---|
| L'app non partiva (`AggregateException` sulla composizione DI) | `AddDbContext` e `AddDbContextFactory` registrati entrambi per `UpsDbContext`: due registrazioni di `DbContextOptions<T>` con lifetime incompatibili (scoped vs singleton) | Un solo punto di registrazione (`AddDbContextFactory`), con `UpsDbContext` scoped derivato dalla factory |
| Il salvataggio di una regola falliva silenziosamente ("Aggiungere almeno una condizione") anche con condizioni compilate nel form | `ShutdownRuleFormDto.SimpleConditions`/`SelectedDays` dichiarate `IReadOnlyList<T>`: il model binder dei form MVC non sa quale tipo concreto istanziare per campi indicizzati (`SimpleConditions[0].Condition`) e lascia la collezione vuota, senza errori | Cambiate in `List<T>`. Aggiunto un test di architettura (`ModelBindingSafetyTests`) che verifica per riflessione che le collezioni nei DTO dei form siano tipi concreti, con controllo negativo eseguito |

## 13.7 Cosa resta per le fasi successive

- Adapter reali (SSH, WinRM, Redfish, QNAP, Wake-on-LAN) — Fase 3. Oggi
  `AdapterRegistration.AddDeviceAdapters` non registra nulla oltre al simulato nemmeno in
  modalità LIVE: il codice è pronto ad accoglierli ma non esiste ancora un adapter reale.
- Retry manuale di una sequenza di spegnimento fallita, dal portale.
- Un editor visuale completo per alberi di condizioni annidati (oggi la modalità avanzata
  richiede di scrivere il JSON a mano per questi casi).
- Verifica end-to-end con un UPS e dispositivi reali: non eseguibile in questo ambiente di
  sviluppo (nessun NUT reale raggiungibile). Verificata invece in modo completo: il protocollo
  NUT contro un server simulato, l'esecuzione delle sequenze contro adapter scriptati, i vincoli
  di database contro un PostgreSQL reale, e l'intero flusso applicativo (login, creazione
  regole/sequenze, verifica dipendenze, audit) contro un'istanza del portale realmente in
  esecuzione.
