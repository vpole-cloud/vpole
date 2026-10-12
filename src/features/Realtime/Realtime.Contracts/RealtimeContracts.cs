namespace Pole.Features.Realtime.Contracts;

public enum RealtimeScope
{
    Tenant,
    Platform,
    /// <summary>An authenticated subject's discovery data, independent of tenant membership.</summary>
    User
}

/// <summary>Registered by the owner, never inferred from a resource-type switch.</summary>
public sealed record RealtimeTopic(
    string Name,
    RealtimeScope Scope);

/// <summary>Only identifiers and versions; HTTP owns authoritative resource data.</summary>
public sealed record RealtimeChange(
    string Topic,
    string? TenantId = null,
    string? ResourceId = null,
    string? ResourceGroupId = null,
    long? Revision = null,
    long? Generation = null,
    long? ObservationVersion = null)
{
    public int Version => 1;
}

public delegate Task PublishRealtimeChange(
    RealtimeChange change,
    CancellationToken cancellationToken = default);
