using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Domain.Models;

public record Redis(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string Name,
    string ResourceGroupId,
    RedisSpec Spec,
    ServiceStatus Status,
    ImmutableHashSet<string> Tags,
    ImmutableList<ServiceEvent> Events,
    string Version
);

public record RedisSpec(
    string KubernetesNamespace,
    Storage Storage,
    Resources Resources
);

public record Storage(
    int SizeGb,
    string StorageClassName
);

public static class RedisFactory
{
    public sealed record RedisCreateOptions(
        string Id,
        ServiceStatus Status,
        ImmutableHashSet<string> Tags,
        ImmutableList<ServiceEvent> Events,
        string Version
    );
    
    public static RedisCreateOptions DefaultCreateOptions =>
        new(
            Id: ObjectId.GenerateNewId().ToString(),
            Status: new Provisioning(),
            Tags: ImmutableHashSet<string>.Empty,
            Events: ImmutableList<ServiceEvent>.Empty,
            Version: Guid.NewGuid().ToString()
        );
    
    public static Redis Create(string name, string resourceGroupId, RedisSpec spec, RedisCreateOptions options) =>
        new(
            Id: options.Id,
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: spec,
            Status: options.Status,
            Tags: options.Tags,
            Events: options.Events,
            Version: options.Version
        );
}