using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Infrastructure.Persistence;

/// <summary>
/// Dati iniziali. Crea la configurazione minima funzionante descritta in §1.2 e la regola
/// "30 + 30" di §9.1, sempre in DRY_RUN.
/// </summary>
public static class SeedData
{
    /// <summary>Nome dell'UPS già installato sul mini PC, come rilevato da <c>upsc</c>.</summary>
    public const string DefaultUpsName = "riello";

    public static async Task SeedAsync(UpsDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await SeedSettingsAsync(db, ct);
        await SeedUpsAsync(db, logger, ct);
        await SeedPolicyAsync(db, logger, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedSettingsAsync(UpsDbContext db, CancellationToken ct)
    {
        var defaults = new (string Key, string? Value, string Type, string Description, bool Sensitive)[]
        {
            // DRY_RUN è il valore iniziale obbligatorio: il sistema non nasce mai in LIVE (§15).
            (SystemSettingKeys.ExecutionMode, nameof(ExecutionMode.DryRun), "enum",
                "Modalità di esecuzione globale. LIVE richiede approvazione esplicita e la " +
                "variabile di deployment UPS_ALLOW_LIVE.", false),

            (SystemSettingKeys.LiveEnabledAdapters, "", "csv",
                "Adapter abilitati a operazioni reali. Vuoto: nessun adapter reale attivo.", false),

            (SystemSettingKeys.MeasurementRetentionDays, "90", "int",
                "Giorni di conservazione delle misure UPS a piena risoluzione.", false),

            (SystemSettingKeys.EventRetentionDays, "365", "int",
                "Giorni di conservazione degli eventi UPS.", false),

            (SystemSettingKeys.AuditRetentionDays, "1825", "int",
                "Giorni di conservazione dell'audit.", false),

            (SystemSettingKeys.AllowedNetworks,
                "10.0.0.0/24,172.16.10.0/24,192.168.98.0/24,192.168.114.0/24", "csv",
                "Reti autorizzate ad accedere al portale.", false),

            (SystemSettingKeys.NutAccessMode, "viaAgent", "enum",
                "Modalità di accesso a NUT dall'orchestrator: 'direct' richiede una LISTEN " +
                "aggiuntiva in upsd.conf, 'viaAgent' non modifica l'host.", false)
        };

        var existing = await db.SystemSettings.Select(s => s.Key).ToListAsync(ct);

        foreach (var (key, value, type, description, sensitive) in defaults)
        {
            if (existing.Contains(key)) continue;

            db.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                ValueType = type,
                Description = description,
                IsSensitive = sensitive,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "seed"
            });
        }
    }

    private static async Task SeedUpsAsync(UpsDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.UpsDevices.AnyAsync(ct)) return;

        // Produttore e modello non vengono precompilati: li popola il primo polling leggendo
        // ups.mfr e ups.model da NUT. Scriverli ora significherebbe mostrare dati non verificati (§5.1).
        db.UpsDevices.Add(new UpsDevice
        {
            Name = "UPS principale",
            Description = "UPS collegato al mini PC server-nut tramite USB seriale FTDI.",
            NutHost = "127.0.0.1",
            NutPort = 3493,
            NutUpsName = DefaultUpsName,
            PollingIntervalSeconds = 10,
            CommunicationTimeoutSeconds = 30,
            StaleDataThresholdSeconds = 60,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        logger.LogInformation("UPS predefinito «{Name}» creato in configurazione.", DefaultUpsName);
    }

    private static async Task SeedPolicyAsync(UpsDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.ShutdownPolicies.AnyAsync(ct)) return;

        var sequence = new ShutdownSequence
        {
            Name = "Sequenza predefinita",
            Description = "Sequenza di esempio in DRY_RUN. Gli step operativi vanno completati " +
                          "con i dispositivi reali prima di qualunque uso in LIVE.",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        sequence.Steps.Add(new ShutdownStep
        {
            OrderIndex = 10,
            Name = "Notifica inizio blackout",
            StepType = SequenceStepType.Notify,
            TimeoutSeconds = 30,
            DelayBeforeSeconds = 0,
            VerifyCompletion = false,
            IsEnabled = true
        });

        db.ShutdownSequences.Add(sequence);

        var policy = new ShutdownPolicy
        {
            Name = "Policy 30+30",
            Description = "Avvia la sequenza alla prima fra: blackout continuo di 30 minuti " +
                          "oppure autonomia residua stimata <= 30 minuti durante lo stato OB.",
            IsActive = true,
            ExecutionMode = ExecutionMode.DryRun,
            MaxBlackoutDurationSeconds = 1800,
            MinRemainingRuntimeSeconds = 1800,
            DebounceSamples = 3,
            UnknownRuntimePolicy = UnknownRuntimePolicy.TreatAsInsufficient,
            UncertainDurationPolicy = UncertainDurationPolicy.TreatAsImmediateThreshold,
            PowerRestoredDuringShutdownPolicy = PowerRestoredDuringShutdownPolicy.CompleteThenRestart,
            CommunicationLostPolicy = CommunicationLostPolicy.HoldAndAlert,
            CommunicationLostGraceSeconds = 120,
            CriticalBatteryChargePercent = 20m,
            TakeoverGraceSeconds = 90,
            DefaultSequence = sequence,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Le due soglie sono espresse anche come regole esplicite, così sono visibili e
        // modificabili nell'editor invece di restare implicite nel motore.
        policy.Rules.Add(new ShutdownRule
        {
            Name = "Blackout continuo oltre 30 minuti",
            Description = "Condizione 1 della regola 30+30.",
            Priority = 100,
            IsEnabled = true,
            ConditionTreeJson = RuleSerialization.SerializeCondition(
                ConditionNode.And(
                    ConditionNode.Flag("OB"),
                    ConditionNode.Numeric(ConditionKind.BlackoutDurationSeconds,
                        ConditionOperator.GreaterThanOrEqual, 1800))),
            TriggerSequence = sequence
        });

        policy.Rules.Add(new ShutdownRule
        {
            Name = "Autonomia residua sotto 30 minuti",
            Description = "Condizione 2 della regola 30+30. Se l'autonomia non è disponibile " +
                          "interviene la UnknownRuntimePolicy, non un valore predefinito.",
            Priority = 110,
            IsEnabled = true,
            ConditionTreeJson = RuleSerialization.SerializeCondition(
                ConditionNode.And(
                    ConditionNode.Flag("OB"),
                    ConditionNode.Numeric(ConditionKind.RemainingRuntimeSeconds,
                        ConditionOperator.LessThanOrEqual, 1800))),
            TriggerSequence = sequence
        });

        db.ShutdownPolicies.Add(policy);

        var startupPolicy = new StartupPolicy
        {
            Name = "Riaccensione predefinita",
            Description = "Richiede alimentazione stabile per 5 minuti su 10 letture consecutive " +
                          "e approvazione manuale.",
            IsActive = true,
            ExecutionMode = ExecutionMode.DryRun,
            RequiredStablePowerSeconds = 300,
            StabilitySamples = 10,
            MinBatteryChargePercent = 50m,
            AntiFlappingCooldownSeconds = 900,
            RequireManualApproval = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.StartupPolicies.Add(startupPolicy);

        logger.LogInformation(
            "Policy «{Policy}» creata in modalità DRY_RUN con soglie 30+30.", policy.Name);
    }
}
