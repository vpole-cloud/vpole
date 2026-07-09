using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.RedisServices.Domain;

public record Redis(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string Name,
    string ResourceGroupId,
    RedisSpec Spec,
    ServiceStatus Status,
    ImmutableHashSet<string> Tags,
    ImmutableList<ServiceEvent> Events
);

public record RedisSpec(
    string KubernetesNamespace,
    Storage Storage,
    Resources Resources
);

public record Storage(
    int SizeGb,
    ResolvedStorageProfile StorageProfile
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
    
    public static Redis Create(string tenantId, string name, string resourceGroupId, RedisSpec spec, RedisCreateOptions options) =>
        new(
            Id: options.Id,
            TenantId: tenantId,
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: spec,
            Status: options.Status,
            Tags: options.Tags,
            Events: options.Events
        );
}
