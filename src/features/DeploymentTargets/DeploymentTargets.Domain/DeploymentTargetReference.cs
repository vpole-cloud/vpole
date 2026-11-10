using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.DeploymentTargets.Domain;

public sealed record DeploymentTargetReference(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentTargetId,
    string OwnerType,
    string OwnerId,
    DateTime CreatedAtUtc
);

public static class DeploymentTargetReferenceFactory
{
    public const int MaximumOwnerTypeLength = 128;
    public const int MaximumOwnerIdLength = 512;

    public static DeploymentTargetReference Create(
        string deploymentTargetId,
        string ownerType,
        string ownerId,
        CreateOptions options) => new(
        DeploymentTargetValueGuard.ObjectId(options.Id, nameof(options.Id)),
        DeploymentTargetValueGuard.ObjectId(deploymentTargetId, nameof(deploymentTargetId)),
        DeploymentTargetValueGuard.Required(ownerType, nameof(ownerType), MaximumOwnerTypeLength),
        DeploymentTargetValueGuard.Required(ownerId, nameof(ownerId), MaximumOwnerIdLength),
        DeploymentTargetValueGuard.Utc(options.CreatedAtUtc, nameof(options.CreatedAtUtc)));

    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc
    );
}
