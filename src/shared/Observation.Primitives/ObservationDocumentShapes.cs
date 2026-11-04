namespace Pole.Observation;

// Interfaces describe common fields without introducing a BSON base class or discriminator.
public interface IObservationControl
{
    string Id { get; }
    long Epoch { get; }
    long SourceControlVersion { get; }
    long ObservationVersion { get; }
    long DemandFence { get; }
    string? InstanceId { get; }
    string? Token { get; }
    DateTime LeaseExpiresAtUtc { get; }
    bool SourceHealthy { get; }
    DateTime? IdleSinceUtc { get; }
    DateTime UpdatedAtUtc { get; }
}

public interface IObservationDemand
{
    string Id { get; }
    string SubscriptionId { get; }
    string ConnectionId { get; }
    string SessionId { get; }
    DateTime OriginalExpiresAtUtc { get; }
    DateTime ExpiresAtUtc { get; }
}

public interface IObservationSnapshot
{
    string Id { get; }
    long SourceControlVersion { get; }
    long ObservationVersion { get; }
    DateTime CheckedAtUtc { get; }
    DateTime? ExpiresAtUtc { get; }
}
