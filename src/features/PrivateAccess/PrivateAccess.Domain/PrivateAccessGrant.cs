using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessGrant(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string SubjectUserId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string EndpointId,
    string CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    long Revision,
    PrivateAccessGrantState State)
{
    public long ReferenceFence { get; init; }

    public bool IsActive(DateTime nowUtc) => State is ActivePrivateAccessGrant && ExpiresAtUtc > nowUtc;

    public PrivateAccessGrant Revoke(string actorUserId, DateTime nowUtc) =>
        State is RevokedPrivateAccessGrant ? this : this with
        {
            State = new RevokedPrivateAccessGrant(nowUtc, actorUserId),
            Revision = checked(Revision + 1)
        };
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ActivePrivateAccessGrant), typeof(RevokedPrivateAccessGrant))]
public closed record PrivateAccessGrantState;

[BsonDiscriminator("Active")]
public sealed record ActivePrivateAccessGrant : PrivateAccessGrantState;

[BsonDiscriminator("Revoked")]
public sealed record RevokedPrivateAccessGrant(
    DateTime RevokedAtUtc,
    string RevokedByUserId) : PrivateAccessGrantState;

public static class PrivateAccessGrantFactory
{
    public static PrivateAccessGrant Create(
        string tenantId,
        string subjectUserId,
        string resourceType,
        string resourceId,
        string deploymentId,
        string endpointId,
        string actorUserId,
        DateTime nowUtc,
        DateTime expiresAtUtc)
    {
        if (expiresAtUtc <= nowUtc)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        return new PrivateAccessGrant(
            ObjectId.GenerateNewId().ToString(),
            tenantId,
            subjectUserId,
            resourceType,
            resourceId,
            deploymentId,
            endpointId,
            actorUserId,
            nowUtc,
            expiresAtUtc,
            1,
            new ActivePrivateAccessGrant());
    }
}
