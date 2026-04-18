using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Domain.Models;

public record ResourceGroup(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string Name,
    string Description,
    string KubernetesNamespace,
    ImmutableList<ShortResourceInfo> Resources,
    ImmutableHashSet<string> Tags
);

public sealed record ShortResourceInfo(
    string Id,
    string Name,
    string Type
);

public static class ResourceGroupFactory
{
    public sealed record ResourceGroupCreateOptions(
        string Id,
        ImmutableList<ShortResourceInfo> Resources,
        ImmutableHashSet<string> Tags
        );
    
    public static ResourceGroupCreateOptions DefaultCreateOptions =>
        new(
            Id: ObjectId.GenerateNewId().ToString(),
            Resources: ImmutableList<ShortResourceInfo>.Empty,
            Tags: ImmutableHashSet<string>.Empty
        );
    
    public static ResourceGroup Create(string name, string description, string kubernetesNamespace, ResourceGroupCreateOptions options) =>
        new(
            Id: options.Id,
            Name: name,
            Description: description,
            KubernetesNamespace: kubernetesNamespace,
            Resources: options.Resources,
            Tags: options.Tags
        );
}