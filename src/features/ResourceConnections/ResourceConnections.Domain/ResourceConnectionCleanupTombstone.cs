using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.ResourceConnections.Domain;

// Hidden from the public connection API. Retained cleanup authority repairs a
// late Kubernetes write even after the public declaration has been removed.
public sealed record ResourceConnectionCleanupTombstone(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    long Generation,
    ImmutableList<ConnectionTargetSnapshot> Targets,
    DateTime CompletedAtUtc);
