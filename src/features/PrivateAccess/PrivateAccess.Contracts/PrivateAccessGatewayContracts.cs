using System.Security.Cryptography;
using System.Text;

namespace Pole.Features.PrivateAccess.Contracts;

public static class PrivateAccessGatewayIds
{
    public static string StableId(string tenantId, string targetId)
    {
        if (!IsCanonicalObjectId(tenantId) || !IsCanonicalObjectId(targetId))
            throw new ArgumentException("Gateway tenant and target IDs must be canonical ObjectIds.");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}:{targetId}"));
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static bool IsCanonicalObjectId(string? value) =>
        value is { Length: 24 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// The current certificate identity of the Access Gateway for one tenant in one target.
/// A generation change invalidates every existing control and data stream.
/// </summary>
public sealed record PrivateAccessGatewayIdentity(
    string TenantId,
    string DeploymentTargetId,
    string GatewayId,
    long Generation,
    long Revision);

public delegate Task<PrivateAccessGatewayIdentity?> ResolvePrivateAccessGatewayIdentity(
    string certificateSha256Pin,
    CancellationToken cancellationToken = default);

public delegate Task<bool> RecordPrivateAccessGatewayContact(
    PrivateAccessGatewayIdentity identity,
    string version,
    CancellationToken cancellationToken = default);

public sealed record PrivateAccessGatewayEndpointSnapshot(
    string EndpointId,
    string RuntimeCellId,
    string ResourceDeploymentId,
    string KubernetesNamespace,
    string ServiceName,
    int ServicePort,
    int TargetPort);

public sealed record PrivateAccessGatewayDesiredState(
    string TenantId,
    string DeploymentTargetId,
    IReadOnlyList<PrivateAccessGatewayEndpointSnapshot> Endpoints);

public sealed record PrivateAccessGatewayRegistration(
    PrivateAccessGatewayIdentity Identity,
    string CertificateSha256Pin,
    DateTime CertificateNotAfterUtc,
    IReadOnlyList<PrivateAccessGatewayEndpointSnapshot> Endpoints);

public delegate Task<IReadOnlyList<PrivateAccessGatewayDesiredState>>
    LoadPrivateAccessGatewayDesiredStates(CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<PrivateAccessGatewayRegistration>>
    LoadPrivateAccessGatewayRegistrations(CancellationToken cancellationToken = default);

public delegate Task<bool> SavePrivateAccessGatewayRegistration(
    PrivateAccessGatewayIdentity identity,
    string certificateSha256Pin,
    DateTime certificateNotAfterUtc,
    IReadOnlyList<PrivateAccessGatewayEndpointSnapshot> endpoints,
    CancellationToken cancellationToken = default);

public delegate Task<bool> RemovePrivateAccessGatewayRegistration(
    PrivateAccessGatewayIdentity identity,
    CancellationToken cancellationToken = default);
