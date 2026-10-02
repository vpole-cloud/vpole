using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessBrokerOwner(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string DeploymentTargetId,
    long GatewayGeneration,
    long GatewayRevision,
    long Epoch,
    string? InstanceId,
    string? InternalOrigin,
    DateTime LeaseExpiresAtUtc)
{
    public static PrivateAccessBrokerOwner Create(
        string gatewayId,
        string tenantId,
        string deploymentTargetId,
        long generation,
        long revision) => new(
            gatewayId, tenantId, deploymentTargetId, generation, revision,
            0, null, null, DateTime.UnixEpoch);

    public PrivateAccessBrokerOwner Reset(long generation, long revision) => this with
    {
        GatewayGeneration = generation,
        GatewayRevision = revision,
        Epoch = checked(Epoch + 1),
        InstanceId = null,
        InternalOrigin = null,
        LeaseExpiresAtUtc = DateTime.UnixEpoch
    };

    public static void ValidateOrigin(string internalOrigin)
    {
        if (!Uri.TryCreate(internalOrigin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
            throw new ArgumentException("The broker's internal origin must be HTTPS.", nameof(internalOrigin));
    }
}
