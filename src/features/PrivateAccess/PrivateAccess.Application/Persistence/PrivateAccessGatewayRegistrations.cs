using MongoDB.Driver;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal sealed class PrivateAccessGatewayRegistrations(
    IMongoCollection<PrivateAccessGateway> gateways)
{
    public async Task<IReadOnlyList<PrivateAccessGatewayRegistration>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await gateways.Find(FilterDefinition<PrivateAccessGateway>.Empty)
            .ToListAsync(cancellationToken);
        return records.Select(ToContract).ToArray();
    }

    public async Task<bool> SaveAsync(
        PrivateAccessGatewayIdentity identity,
        string certificateSha256Pin,
        DateTime certificateNotAfterUtc,
        IReadOnlyList<PrivateAccessGatewayEndpointSnapshot> endpoints,
        CancellationToken cancellationToken = default)
    {
        if (identity.GatewayId != PrivateAccessGatewayIds.StableId(
                identity.TenantId, identity.DeploymentTargetId) ||
            identity.Generation < 1 || identity.Revision < 1 ||
            certificateSha256Pin.Length != 64 ||
            !certificateSha256Pin.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            certificateNotAfterUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Invalid Private Access gateway registration.");

        var routes = endpoints.Select(ToDomain).ToArray();
        var filter = PrivateAccessGatewayFilters.ByTenantTarget(
            identity.TenantId, identity.DeploymentTargetId);
        var current = await gateways.Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            if (identity.Generation != 1 || identity.Revision != 1)
                return false;
            var created = PrivateAccessGatewayFactory.Create(
                identity.GatewayId, identity.TenantId, identity.DeploymentTargetId,
                certificateSha256Pin, certificateNotAfterUtc, routes);
            try
            {
                await gateways.InsertOneAsync(created, cancellationToken: cancellationToken);
                return true;
            }
            catch (MongoWriteException exception) when (
                exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return false;
            }
        }

        if (current.Id != identity.GatewayId ||
            current.State is not ActivePrivateAccessGatewayState)
            return false;
        PrivateAccessGateway replacement;
        if (current.Generation == identity.Generation &&
            current.Revision == identity.Revision &&
            current.CertificateSha256Pin == certificateSha256Pin &&
            current.CertificateNotAfterUtc == certificateNotAfterUtc)
        {
            if (current.Endpoints.SequenceEqual(routes))
                return true;
            replacement = current with
            {
                Endpoints = routes,
                Revision = checked(current.Revision + 1)
            };
        }
        else if (identity.Generation == current.Generation + 1 &&
                 identity.Revision == current.Revision + 1)
        {
            replacement = current.Rotate(certificateSha256Pin, certificateNotAfterUtc)
                with
            { Endpoints = routes };
        }
        else
        {
            return false;
        }

        var updated = await gateways.ReplaceOneAsync(
            PrivateAccessGatewayFilters.Current(
                current.TenantId, current.DeploymentTargetId,
                current.Id, current.Generation, current.Revision),
            replacement, cancellationToken: cancellationToken);
        return updated.MatchedCount == 1;
    }

    public async Task<bool> RemoveAsync(
        PrivateAccessGatewayIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var result = await gateways.DeleteOneAsync(
            PrivateAccessGatewayFilters.Current(
                identity.TenantId, identity.DeploymentTargetId,
                identity.GatewayId, identity.Generation, identity.Revision),
            cancellationToken);
        return result.DeletedCount == 1;
    }

    private static PrivateAccessGatewayRegistration ToContract(PrivateAccessGateway gateway) =>
        new(
            new PrivateAccessGatewayIdentity(
                gateway.TenantId, gateway.DeploymentTargetId,
                gateway.Id, gateway.Generation, gateway.Revision),
            gateway.CertificateSha256Pin,
            gateway.CertificateNotAfterUtc,
            gateway.Endpoints.Select(endpoint => new PrivateAccessGatewayEndpointSnapshot(
                endpoint.EndpointId, endpoint.RuntimeCellId,
                endpoint.ResourceDeploymentId, endpoint.KubernetesNamespace,
                endpoint.ServiceName, endpoint.ServicePort, endpoint.TargetPort)).ToArray());

    private static PrivateAccessGatewayRoute ToDomain(
        PrivateAccessGatewayEndpointSnapshot endpoint) => new(
        endpoint.EndpointId, endpoint.RuntimeCellId,
        endpoint.ResourceDeploymentId, endpoint.KubernetesNamespace,
        endpoint.ServiceName, endpoint.ServicePort, endpoint.TargetPort);
}
