using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.PrivateAccess.Domain;

public sealed record PrivateAccessGateway(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string DeploymentTargetId,
    string CertificateSha256Pin,
    DateTime CertificateNotAfterUtc,
    long Generation,
    long Revision,
    DateTime? LastContactAtUtc,
    string? Version,
    PrivateAccessGatewayState State,
    IReadOnlyList<PrivateAccessGatewayRoute> Endpoints) : ITenantOwned
{
    public PrivateAccessGateway Rotate(
        string certificateSha256Pin,
        DateTime certificateNotAfterUtc) => this with
        {
            CertificateSha256Pin = certificateSha256Pin,
            CertificateNotAfterUtc = certificateNotAfterUtc,
            Generation = checked(Generation + 1),
            Revision = checked(Revision + 1),
            LastContactAtUtc = null,
            Version = null
        };

    public PrivateAccessGateway Revoke(DateTime atUtc) => State is RevokedPrivateAccessGatewayState
        ? this
        : this with
        {
            State = new RevokedPrivateAccessGatewayState(atUtc),
            Revision = checked(Revision + 1)
        };
}

public sealed record PrivateAccessGatewayRoute(
    string EndpointId,
    string RuntimeCellId,
    string ResourceDeploymentId,
    string KubernetesNamespace,
    string ServiceName,
    int ServicePort,
    int TargetPort);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ActivePrivateAccessGatewayState), typeof(RevokedPrivateAccessGatewayState))]
public closed record PrivateAccessGatewayState;

[BsonDiscriminator("Active")]
public sealed record ActivePrivateAccessGatewayState : PrivateAccessGatewayState;

[BsonDiscriminator("Revoked")]
public sealed record RevokedPrivateAccessGatewayState(
    DateTime RevokedAtUtc) : PrivateAccessGatewayState;

public static class PrivateAccessGatewayFactory
{
    public static PrivateAccessGateway Create(
        string id,
        string tenantId,
        string deploymentTargetId,
        string certificateSha256Pin,
        DateTime certificateNotAfterUtc,
        IReadOnlyList<PrivateAccessGatewayRoute> endpoints) => new(
            id,
            tenantId,
            deploymentTargetId,
            certificateSha256Pin,
            certificateNotAfterUtc,
            1,
            1,
            null,
            null,
            new ActivePrivateAccessGatewayState(),
            endpoints);
}
