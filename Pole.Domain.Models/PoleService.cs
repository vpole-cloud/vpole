using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Domain.Models.Abstractions;

namespace Pole.Domain.Models;

public abstract record PoleService(
    string Name,
    string ResourceGroupId,
    string Namespace
) : BaseEntity
{
    public ServiceStatus Status { get; init; } = new Provisioning();
    public ImmutableHashSet<string> Tags { get; init; } = ImmutableHashSet<string>.Empty;
    public ImmutableList<ServiceEvent> Events { get; init; } = ImmutableList<ServiceEvent>.Empty;
    public string Version { get; init; } = Guid.NewGuid().ToString("N");
}

[BsonDiscriminator(RootClass =  true)]
[BsonKnownTypes(typeof(Provisioning), typeof(Running), typeof(Failed), typeof(Stopped))]
public abstract record ServiceStatus
{
    public DateTime LastUpdatedOn { get; init; } = DateTime.UtcNow;
}

public sealed record Provisioning : ServiceStatus;
public sealed record Running : ServiceStatus;
public sealed record Failed(string Reason) : ServiceStatus;
public sealed record Stopped : ServiceStatus;

public record ServiceEvent(
    DateTime OccurredOn,
    string Message
);