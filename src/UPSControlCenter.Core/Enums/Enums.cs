namespace UPSControlCenter.Core.Enums;

/// <summary>Modalità di esecuzione globale. <see cref="DryRun"/> è l'unico valore iniziale ammesso.</summary>
public enum ExecutionMode
{
    DryRun = 0,
    Live = 1
}

public enum DeviceType
{
    Generic = 0,
    WindowsServer = 1,
    LinuxServer = 2,
    DockerHost = 3,
    NasQnap = 4,
    LenovoXClarity = 5,
    NetworkAppliance = 6,
    NutHost = 7
}

/// <summary>Protocollo di spegnimento. I protocolli reali restano inattivi in DRY_RUN.</summary>
public enum ShutdownProtocol
{
    None = 0,
    Ssh = 1,
    WinRm = 2,
    Redfish = 3,
    QnapSsh = 4,
    QnapApi = 5,
    HttpsApi = 6,
    RemoteAgent = 7
}

public enum StartupProtocol
{
    None = 0,
    WakeOnLan = 1,
    Redfish = 2,
    Bmc = 3,
    BiosRestoreOnAcLoss = 4,
    HttpsApi = 5,
    RemoteAgent = 6
}

public enum StatusCheckMethod
{
    None = 0,
    Ping = 1,
    TcpPort = 2,
    Ssh = 3,
    WinRm = 4,
    Redfish = 5,
    HttpProbe = 6,
    Composite = 7
}

public enum DeviceState
{
    Unknown = 0,
    On = 1,
    Off = 2,
    ShuttingDown = 3,
    Starting = 4,
    Unreachable = 5,
    Error = 6
}

public enum DependencyType
{
    Generic = 0,
    StorageNfs = 1,
    StorageSmb = 2,
    Database = 3,
    Network = 4,
    Application = 5
}

public enum CredentialAuthType
{
    Password = 0,
    SshKey = 1,
    ApiToken = 2,
    Certificate = 3
}

public enum SequenceStepType
{
    Notify = 0,
    ShutdownDevice = 1,
    ShutdownGroup = 2,
    StartupDevice = 3,
    StartupGroup = 4,
    Wait = 5,
    VerifyState = 6,
    Conditional = 7,
    ShutdownNutHost = 8
}

public enum RunKind
{
    Shutdown = 0,
    Startup = 1,
    Simulation = 2
}

public enum RunState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Aborted = 4,
    Superseded = 5
}

public enum ActionState
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
    TimedOut = 5
}

public enum WorkerRole
{
    Orchestrator = 0,
    SafetyAgent = 1,
    Simulator = 2
}

public enum ConfigurationState
{
    Draft = 0,
    Validated = 1,
    Published = 2,
    Superseded = 3,
    RolledBack = 4,
    Rejected = 5
}

public enum UpsEventType
{
    BlackoutStarted = 0,
    PowerRestored = 1,
    LowBattery = 2,
    BatteryCritical = 3,
    CommunicationLost = 4,
    CommunicationRestored = 5,
    StaleData = 6,
    ThresholdCrossed = 7,
    SequenceStarted = 8,
    SequenceCompleted = 9,
    SequenceFailed = 10,
    DeviceError = 11,
    OrchestratorUnavailable = 12,
    TakeoverPerformed = 13,
    BlackoutDurationUncertain = 14,
    ConfigurationRejected = 15,
    ConfigSyncStale = 16,
    AutomationDisabled = 17,
    RecoveryCompleted = 18,
    RuntimeUnknown = 19,
    LoadAnomaly = 20
}

public enum EventSeverity
{
    Information = 0,
    Warning = 1,
    Critical = 2
}

public enum NotificationChannelType
{
    Smtp = 0,
    Webhook = 1,
    Teams = 2
}

/// <summary>
/// Politica da applicare quando l'autonomia residua non è nota (variabile NUT assente o non
/// numerica). Un valore mancante non viene mai interpretato come zero (§9.3).
/// </summary>
public enum UnknownRuntimePolicy
{
    /// <summary>Ignora la condizione di autonomia; restano attive le altre (durata, LB).</summary>
    IgnoreRuntimeCondition = 0,
    /// <summary>Considera l'autonomia come insufficiente: scelta conservativa.</summary>
    TreatAsInsufficient = 1,
    /// <summary>Usa la carica batteria come indicatore sostitutivo, se disponibile.</summary>
    FallbackToBatteryCharge = 2
}

/// <summary>Politica applicata quando la durata del blackout non è ricostruibile dopo un riavvio.</summary>
public enum UncertainDurationPolicy
{
    /// <summary>Riparte da zero: meno sicuro, documentato come non predefinito.</summary>
    RestartTimer = 0,
    /// <summary>Considera la soglia di durata già raggiunta. Predefinito, conservativo (§9.4).</summary>
    TreatAsImmediateThreshold = 1,
    /// <summary>Entra in sospensione e richiede intervento manuale.</summary>
    RequireManualDecision = 2
}

/// <summary>Politica per il ritorno della corrente mentre una sequenza di spegnimento è in corso (§11, caso B).</summary>
public enum PowerRestoredDuringShutdownPolicy
{
    /// <summary>Completa la sequenza, poi avvia il ripristino. Predefinito: evita stati misti.</summary>
    CompleteThenRestart = 0,
    /// <summary>Interrompe prima dello step successivo, senza annullare gli step già avviati.</summary>
    StopAtNextStepBoundary = 1,
    /// <summary>Completa la sequenza e non riaccende automaticamente.</summary>
    CompleteAndHold = 2
}

/// <summary>Politica quando la comunicazione con NUT è persa o i dati sono obsoleti.</summary>
public enum CommunicationLostPolicy
{
    /// <summary>Mantiene l'ultimo stato noto e segnala; non avvia sequenze. Predefinito.</summary>
    HoldAndAlert = 0,
    /// <summary>Tratta la perdita di comunicazione come blackout in corso dopo una grazia configurata.</summary>
    TreatAsBlackoutAfterGrace = 1
}
