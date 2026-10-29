using UPSControlCenter.Core.Safety;

namespace UPSControlCenter.Contracts.Dto;

/// <summary>
/// Payload restituito al Safety Agent dall'endpoint di sincronizzazione.
/// Contiene il JSON canonico, non l'oggetto, perché il checksum va verificato esattamente sui
/// byte ricevuti: una ri-serializzazione lato agent potrebbe produrre un risultato diverso (§3.3).
/// </summary>
public sealed record SafetyConfigEnvelope
{
    public required long Version { get; init; }

    /// <summary>JSON canonico della <see cref="SafetyConfiguration"/>.</summary>
    public required string CanonicalPayload { get; init; }

    public required string ChecksumSha256 { get; init; }

    /// <summary>Firma HMAC-SHA256 codificata in Base64.</summary>
    public required string SignatureBase64 { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }
}

public sealed record ConfigAckRequest
{
    public required long Version { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Heartbeat inviato dal Safety Agent al Control Plane. Best-effort: l'agent non dipende dall'esito.</summary>
public sealed record AgentHeartbeatRequest
{
    public required string InstanceId { get; init; }
    public required string State { get; init; }
    public required long ActiveConfigVersion { get; init; }
    public required long FencingToken { get; init; }
    public string? CurrentBlackoutId { get; init; }
    public int? BlackoutDurationSeconds { get; init; }
    public bool AutomationDisabled { get; init; }
    public string? LastUpsStatus { get; init; }
}
