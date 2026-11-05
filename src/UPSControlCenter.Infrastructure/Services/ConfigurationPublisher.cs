using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Enums;
using UPSControlCenter.Core.Safety;
using UPSControlCenter.Infrastructure.Options;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Infrastructure.Services;

public sealed record PublishResult
{
    public required bool Success { get; init; }
    public long? Version { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IConfigurationPublisher
{
    /// <summary>Costruisce la configurazione di sicurezza dallo stato corrente del database.</summary>
    Task<SafetyConfiguration?> BuildAsync(CancellationToken ct = default);

    /// <summary>Valida e pubblica una nuova versione. Operazione atomica (§3.3).</summary>
    Task<PublishResult> PublishAsync(string userName, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Ripubblica il payload di una versione precedente con un <b>nuovo</b> numero di versione:
    /// il rollback segue lo stesso percorso di un aggiornamento, così la regola "versione
    /// crescente" resta sempre valida sull'agent (§7.4).
    /// </summary>
    Task<PublishResult> RollbackToAsync(long version, string userName, string reason,
        CancellationToken ct = default);
}

public sealed class ConfigurationPublisher(
    UpsDbContext db,
    IOptions<SafetySyncOptions> syncOptions,
    SafetyConfigurationValidator validator,
    IAuditService audit,
    ILogger<ConfigurationPublisher> logger) : IConfigurationPublisher
{
    public async Task<SafetyConfiguration?> BuildAsync(CancellationToken ct = default)
    {
        var policy = await db.ShutdownPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.IsActive, ct);

        if (policy is null)
        {
            logger.LogWarning("Nessuna policy di spegnimento attiva: impossibile costruire la configurazione.");
            return null;
        }

        var ups = await db.UpsDevices
            .AsNoTracking()
            .Where(u => u.IsEnabled)
            .OrderBy(u => u.Name)
            .FirstOrDefaultAsync(ct);

        if (ups is null)
        {
            logger.LogWarning("Nessun UPS abilitato: impossibile costruire la configurazione.");
            return null;
        }

        var devices = await db.ManagedDevices
            .AsNoTracking()
            .OrderBy(d => d.Priority).ThenBy(d => d.Name)
            .ToListAsync(ct);

        var dependencies = await db.DeviceDependencies.AsNoTracking().ToListAsync(ct);

        var steps = policy.DefaultSequenceId is { } seqId
            ? await db.ShutdownSteps
                .AsNoTracking()
                .Where(s => s.ShutdownSequenceId == seqId && s.IsEnabled)
                .OrderBy(s => s.OrderIndex)
                .ToListAsync(ct)
            : [];

        var deviceIds = devices.Select(d => d.Id).ToHashSet();
        var groupMembers = devices
            .Where(d => d.DeviceGroupId is not null)
            .GroupBy(d => d.DeviceGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Id).ToList());

        var mode = await GetModeAsync(ct);

        return new SafetyConfiguration
        {
            Version = await NextVersionAsync(ct),
            GeneratedAt = DateTimeOffset.UtcNow,
            ExecutionMode = mode.Mode,
            LiveEnabledAdapters = mode.Adapters,
            TakeoverGraceSeconds = policy.TakeoverGraceSeconds,
            Ups = new SafetyUpsConfig
            {
                // Il Safety Agent legge NUT in locale: l'host remoto non è rilevante per lui.
                NutHost = "127.0.0.1",
                NutPort = ups.NutPort,
                NutUpsName = ups.NutUpsName,
                PollingIntervalSeconds = ups.PollingIntervalSeconds,
                CommunicationTimeoutSeconds = ups.CommunicationTimeoutSeconds,
                StaleDataThresholdSeconds = ups.StaleDataThresholdSeconds
            },
            Thresholds = new SafetyThresholds
            {
                MaxBlackoutDurationSeconds = policy.MaxBlackoutDurationSeconds,
                MinRemainingRuntimeSeconds = policy.MinRemainingRuntimeSeconds,
                DebounceSamples = policy.DebounceSamples,
                CriticalBatteryChargePercent = policy.CriticalBatteryChargePercent,
                UnknownRuntimePolicy = policy.UnknownRuntimePolicy,
                UncertainDurationPolicy = policy.UncertainDurationPolicy,
                CommunicationLostPolicy = policy.CommunicationLostPolicy,
                CommunicationLostGraceSeconds = policy.CommunicationLostGraceSeconds,
                PowerRestoredDuringShutdownPolicy = policy.PowerRestoredDuringShutdownPolicy
            },
            Devices = [.. devices.Select(d => new SafetyDevice
            {
                Id = d.Id,
                Name = d.Name,
                Hostname = d.Hostname,
                DeviceType = d.DeviceType,
                ShutdownProtocol = d.ShutdownProtocol,
                Priority = d.Priority,
                ShutdownTimeoutSeconds = d.ShutdownTimeoutSeconds,
                PostActionDelaySeconds = d.PostActionDelaySeconds,
                StatusCheckMethod = d.StatusCheckMethod,
                IsEnabled = d.IsEnabled
            })],
            ShutdownSteps = [.. steps.Select(s => new SafetyStep
            {
                Id = s.Id,
                OrderIndex = s.OrderIndex,
                Name = s.Name,
                StepType = s.StepType,
                TargetDeviceId = s.TargetDeviceId,
                TargetDeviceIds = ResolveTargets(s, groupMembers, deviceIds),
                ParallelGroup = s.ParallelGroup,
                DelayBeforeSeconds = s.DelayBeforeSeconds,
                TimeoutSeconds = s.TimeoutSeconds,
                MaxRetries = s.MaxRetries,
                ContinueOnError = s.ContinueOnError,
                VerifyCompletion = s.VerifyCompletion
            })],
            Dependencies = [.. dependencies.Select(d => new SafetyDependency
            {
                DependentDeviceId = d.DependentDeviceId,
                DependsOnDeviceId = d.DependsOnDeviceId,
                DependencyType = d.DependencyType,
                IsHard = d.IsHard
            })]
        };
    }

    /// <summary>
    /// Risolve i bersagli di uno step espandendo i gruppi e rimuovendo le esclusioni. L'agent
    /// riceve id espliciti, così non deve risolvere gruppi senza accesso al database.
    /// </summary>
    private static IReadOnlyList<Guid> ResolveTargets(ShutdownStep step,
        Dictionary<Guid, List<Guid>> groupMembers, HashSet<Guid> knownDevices)
    {
        var targets = new List<Guid>();

        if (step.TargetDeviceId is { } single && knownDevices.Contains(single))
            targets.Add(single);

        if (step.TargetGroupId is { } groupId && groupMembers.TryGetValue(groupId, out var members))
            targets.AddRange(members);

        return [.. targets.Distinct().Where(id => !step.ExcludedDeviceIds.Contains(id))];
    }

    private async Task<(ExecutionMode Mode, List<string> Adapters)> GetModeAsync(CancellationToken ct)
    {
        var modeRaw = await db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.ExecutionMode)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        var mode = Enum.TryParse<ExecutionMode>(modeRaw, true, out var m) ? m : ExecutionMode.DryRun;

        var adaptersRaw = await db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.LiveEnabledAdapters)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        var adapters = mode == ExecutionMode.Live && !string.IsNullOrWhiteSpace(adaptersRaw)
            ? adaptersRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];

        return (mode, adapters);
    }

    private async Task<long> NextVersionAsync(CancellationToken ct)
    {
        var max = await db.ConfigurationVersions.MaxAsync(c => (long?)c.Version, ct) ?? 0;
        return max + 1;
    }

    public async Task<PublishResult> PublishAsync(string userName, string? reason,
        CancellationToken ct = default)
    {
        var config = await BuildAsync(ct);
        if (config is null)
        {
            return new PublishResult
            {
                Success = false,
                Errors = ["Impossibile costruire la configurazione: servono una policy di " +
                          "spegnimento attiva e almeno un UPS abilitato."]
            };
        }

        var validation = validator.Validate(config);
        if (!validation.IsValid)
        {
            // La configurazione non valida viene registrata come rifiutata, non scartata in
            // silenzio: l'utente deve poter vedere perché la pubblicazione non è avvenuta.
            db.ConfigurationVersions.Add(new ConfigurationVersion
            {
                Version = config.Version,
                PayloadJson = ConfigurationIntegrity.Canonicalize(config),
                ChecksumSha256 = string.Empty,
                Signature = [],
                State = ConfigurationState.Rejected,
                CreatedBy = userName,
                CreatedAt = DateTimeOffset.UtcNow,
                ValidationErrorsJson = System.Text.Json.JsonSerializer.Serialize(validation.Errors)
            });

            await audit.RecordAsync(new AuditEntry
            {
                Action = "Configuration.PublishRejected",
                EntityType = nameof(ConfigurationVersion),
                EntityId = config.Version.ToString(),
                UserName = userName,
                Outcome = "Rejected",
                Reason = reason,
                AfterJson = System.Text.Json.JsonSerializer.Serialize(validation.Errors)
            }, ct);

            await db.SaveChangesAsync(ct);

            return new PublishResult
            {
                Success = false,
                Errors = validation.Errors,
                Warnings = validation.Warnings
            };
        }

        return await PersistAsync(config, userName, reason, validation.Warnings, ct);
    }

    /// <summary>
    /// Scrive la nuova versione in una singola transazione serializzabile: l'indice univoco
    /// parziale su <c>Published</c> rende impossibile avere due versioni pubblicate (§4.5).
    /// </summary>
    private async Task<PublishResult> PersistAsync(SafetyConfiguration config, string userName,
        string? reason, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        var payload = ConfigurationIntegrity.Canonicalize(config);
        var checksum = ConfigurationIntegrity.ComputeChecksum(payload);
        var signature = ConfigurationIntegrity.ComputeSignature(payload, syncOptions.Value.GetHmacKey());

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var previous = await db.ConfigurationVersions
            .Where(c => c.State == ConfigurationState.Published)
            .ToListAsync(ct);

        foreach (var p in previous)
            p.State = ConfigurationState.Superseded;

        db.ConfigurationVersions.Add(new ConfigurationVersion
        {
            Version = config.Version,
            PayloadJson = payload,
            ChecksumSha256 = checksum,
            Signature = signature,
            State = ConfigurationState.Published,
            CreatedBy = userName,
            CreatedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow
        });

        await audit.RecordAsync(new AuditEntry
        {
            Action = "Configuration.Published",
            EntityType = nameof(ConfigurationVersion),
            EntityId = config.Version.ToString(),
            UserName = userName,
            Outcome = "Success",
            Reason = reason,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                config.Version,
                config.ExecutionMode,
                Checksum = checksum
            })
        }, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Configurazione di sicurezza versione {Version} pubblicata da {User} in modalità {Mode}.",
            config.Version, userName, config.ExecutionMode);

        return new PublishResult { Success = true, Version = config.Version, Warnings = warnings };
    }

    public async Task<PublishResult> RollbackToAsync(long version, string userName, string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var source = await db.ConfigurationVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Version == version, ct);

        if (source is null)
            return new PublishResult { Success = false, Errors = [$"Versione {version} inesistente."] };

        if (source.State == ConfigurationState.Rejected)
            return new PublishResult
            {
                Success = false,
                Errors = [$"La versione {version} è stata rifiutata in validazione e non può " +
                          "essere ripristinata."]
            };

        var config = System.Text.Json.JsonSerializer.Deserialize<SafetyConfiguration>(
            source.PayloadJson, Core.Rules.RuleSerialization.Options);

        if (config is null)
            return new PublishResult
            {
                Success = false,
                Errors = [$"Il payload della versione {version} non è deserializzabile."]
            };

        // Nuovo numero di versione: l'agent accetta solo versioni crescenti (§7.4).
        var rolled = config with
        {
            Version = await NextVersionAsync(ct),
            GeneratedAt = DateTimeOffset.UtcNow
        };

        var validation = validator.Validate(rolled);
        if (!validation.IsValid)
            return new PublishResult { Success = false, Errors = validation.Errors };

        var result = await PersistAsync(rolled, userName,
            $"Rollback alla versione {version}. {reason}", validation.Warnings, ct);

        if (result.Success)
        {
            var old = await db.ConfigurationVersions.FirstOrDefaultAsync(c => c.Version == version, ct);
            if (old is not null && old.State == ConfigurationState.Superseded)
                old.State = ConfigurationState.RolledBack;
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}
