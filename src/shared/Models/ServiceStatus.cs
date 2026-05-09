using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.Shared.Models;

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