using UPSControlCenter.Core.Entities;
using UPSControlCenter.Core.Rules;

namespace UPSControlCenter.Core.Sequencing;

/// <summary>
/// Converte gli step persistiti (<see cref="ShutdownStep"/>, <see cref="StartupStep"/>) in
/// <see cref="ExecutableStep"/>, risolvendo i gruppi di dispositivi e le esclusioni.
/// <para>
/// È il punto in cui il modello di configurazione (pensato per l'editor) incontra il modello di
/// esecuzione (pensato per <see cref="SequenceExecutor"/>): tenerli separati permette all'editor
/// di evolvere senza toccare il motore.
/// </para>
/// </summary>
public static class SequenceStepResolver
{
    public static IReadOnlyList<ExecutableStep> Resolve(IEnumerable<ShutdownStep> steps,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> groupMembers) =>
        [.. steps
            .Where(s => s.IsEnabled)
            .Select(s => new ExecutableStep
            {
                Id = s.Id,
                OrderIndex = s.OrderIndex,
                Name = s.Name,
                StepType = s.StepType,
                TargetDeviceIds = ResolveTargets(s.TargetDeviceId, s.TargetGroupId,
                    s.ExcludedDeviceIds, groupMembers),
                ParallelGroup = s.ParallelGroup,
                DelayBeforeSeconds = s.DelayBeforeSeconds,
                TimeoutSeconds = s.TimeoutSeconds,
                MaxRetries = s.MaxRetries,
                ContinueOnError = s.ContinueOnError,
                VerifyCompletion = s.VerifyCompletion,
                Condition = RuleSerialization.DeserializeCondition(s.ConditionJson)
            })];

    public static IReadOnlyList<ExecutableStep> Resolve(IEnumerable<StartupStep> steps,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> groupMembers) =>
        [.. steps
            .Where(s => s.IsEnabled)
            .Select(s => new ExecutableStep
            {
                Id = s.Id,
                OrderIndex = s.OrderIndex,
                Name = s.Name,
                StepType = s.StepType,
                TargetDeviceIds = ResolveTargets(s.TargetDeviceId, s.TargetGroupId,
                    s.ExcludedDeviceIds, groupMembers),
                ParallelGroup = s.ParallelGroup,
                DelayBeforeSeconds = s.DelayBeforeSeconds,
                TimeoutSeconds = s.TimeoutSeconds,
                MaxRetries = s.MaxRetries,
                ContinueOnError = s.ContinueOnError,
                VerifyCompletion = s.VerifyCompletion,
                Condition = RuleSerialization.DeserializeCondition(s.ConditionJson)
            })];

    private static IReadOnlyList<Guid> ResolveTargets(Guid? targetDeviceId, Guid? targetGroupId,
        IReadOnlyList<Guid> excluded, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> groupMembers)
    {
        var targets = new List<Guid>();

        if (targetDeviceId is { } single) targets.Add(single);

        if (targetGroupId is { } groupId && groupMembers.TryGetValue(groupId, out var members))
            targets.AddRange(members);

        return [.. targets.Distinct().Where(id => !excluded.Contains(id))];
    }
}
