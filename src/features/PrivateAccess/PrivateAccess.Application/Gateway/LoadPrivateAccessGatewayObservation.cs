using MongoDB.Driver;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Gateway;

public sealed record PrivateAccessGatewayObservation(
    long CredentialGeneration,
    DateTime CredentialExpiresAtUtc,
    DateTime? LastContactAtUtc,
    string? ProtocolVersion,
    bool BrokerLeaseActive
);

public delegate Task<PrivateAccessGatewayObservation?> LoadPrivateAccessGatewayObservation(
    string tenantId,
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadPrivateAccessGatewayObservation(
    IMongoCollection<PrivateAccessGateway> gateways,
    LoadPrivateAccessBrokerOwner loadOwner
)
{
    public async Task<PrivateAccessGatewayObservation?> InvokeAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var gateway = await gateways.Find(PrivateAccessGatewayFilters.ByTenantTarget(tenantId, deploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (gateway is null)
            return null;

        var owner = await loadOwner(tenantId, deploymentTargetId, cancellationToken);
        var leaseActive = gateway.State is ActivePrivateAccessGatewayState && owner?.Gateway is { } identity &&
            identity.GatewayId == gateway.Id && identity.Generation == gateway.Generation &&
            identity.Revision == gateway.Revision;
        return new PrivateAccessGatewayObservation(
            gateway.Generation,
            gateway.CertificateNotAfterUtc,
            gateway.LastContactAtUtc,
            gateway.Version,
            leaseActive);
    }
}
