namespace UPSControlCenter.Orchestrator;

public sealed class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>Nome con cui l'orchestrator registra il proprio heartbeat (§1.4).</summary>
    public string WorkerName { get; set; } = "ups-orchestrator";

    public int HeartbeatIntervalSeconds { get; set; } = 15;

    /// <summary>File sentinella aggiornato a ogni ciclo, usato come health check del container (§6.4).</summary>
    public string HealthSentinelPath { get; set; } = "/tmp/healthy";

    public string KillSwitchPath { get; set; } = "/var/lib/ups-control-center/AUTOMATION_DISABLED";

    /// <summary>
    /// Giorni di conservazione delle misure a piena risoluzione. Il valore effettivo viene letto
    /// da <c>SystemSettings</c>; questo è il ripiego se l'impostazione è assente.
    /// </summary>
    public int DefaultMeasurementRetentionDays { get; set; } = 90;

    public int MaintenanceIntervalMinutes { get; set; } = 60;
}
