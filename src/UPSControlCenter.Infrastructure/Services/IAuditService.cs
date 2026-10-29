using UPSControlCenter.Core.Entities;
using UPSControlCenter.Infrastructure.Persistence;

namespace UPSControlCenter.Infrastructure.Services;

public sealed record AuditEntry
{
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public string? IpAddress { get; init; }
    public string? BeforeJson { get; init; }
    public string? AfterJson { get; init; }
    public required string Outcome { get; init; }
    public string? Reason { get; init; }
}

public interface IAuditService
{
    /// <summary>
    /// Accoda una voce di audit nel contesto corrente. Non chiama <c>SaveChanges</c>: l'audit
    /// deve essere scritto nella stessa transazione dell'operazione che descrive, altrimenti
    /// potrebbe esistere un'operazione senza traccia o una traccia senza operazione.
    /// </summary>
    Task RecordAsync(AuditEntry entry, CancellationToken ct = default);
}

public sealed class AuditService(UpsDbContext db) : IAuditService
{
    public Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        db.AuditLogs.Add(new AuditLog
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            UserId = entry.UserId,
            UserName = entry.UserName,
            IpAddress = entry.IpAddress,
            BeforeJson = entry.BeforeJson,
            AfterJson = entry.AfterJson,
            Outcome = entry.Outcome,
            Reason = entry.Reason
        });

        return Task.CompletedTask;
    }
}
