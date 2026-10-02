using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal sealed class PrivateAccessGatewayRegistrations(
    IMongoCollection<PrivateAccessGateway> gateways,
    IMongoCollection<PrivateAccessBrokerOwner> owners,
    MongoRunTransaction transactions,
    ApplyDeploymentTargetReferenceChanges targetReferences)
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
        return await transactions.InvokeOutcomeAsync<bool>(async (transaction, ct) =>
        {
            var current = await gateways.Find(transaction.Session, filter)
                .FirstOrDefaultAsync(ct);
            if (current is null)
            {
                if (identity.Generation != 1 || identity.Revision != 1)
                    return transaction.Abort(false);
                var reference = await targetReferences(
                    GatewayOwner(identity), [identity.DeploymentTargetId], [],
                    transaction.Session, ct);
                if (reference is DeploymentTargetReferenceChangeRejected)
                    return transaction.Abort(false);
                var created = PrivateAccessGatewayFactory.Create(
                    identity.GatewayId, identity.TenantId, identity.DeploymentTargetId,
                    certificateSha256Pin, certificateNotAfterUtc, routes);
                await gateways.InsertOneAsync(transaction.Session, created,
                    cancellationToken: ct);
                var previousOwner = await owners.Find(transaction.Session,
                        PrivateAccessBrokerOwnerFilters.ByGateway(identity.GatewayId))
                    .FirstOrDefaultAsync(ct);
                if (previousOwner is null)
                    await owners.InsertOneAsync(transaction.Session,
                        PrivateAccessBrokerOwner.Create(
                            identity.GatewayId, identity.TenantId,
                            identity.DeploymentTargetId, identity.Generation,
                            identity.Revision), cancellationToken: ct);
                else
                {
                    var reset = await owners.ReplaceOneAsync(transaction.Session,
                        PrivateAccessBrokerOwnerFilters.Current(previousOwner),
                        previousOwner.Reset(identity.Generation, identity.Revision),
                        cancellationToken: ct);
                    if (reset.MatchedCount != 1)
                        return transaction.Abort(false);
                }
                return transaction.Commit(true);
            }
            if (current.Id != identity.GatewayId ||
                current.State is not ActivePrivateAccessGatewayState)
                return transaction.Abort(false);
            PrivateAccessGateway replacement;
            if (current.Generation == identity.Generation &&
                current.Revision == identity.Revision &&
                current.CertificateSha256Pin == certificateSha256Pin &&
                current.CertificateNotAfterUtc == certificateNotAfterUtc)
            {
                if (current.Endpoints.SequenceEqual(routes))
                    return transaction.Commit(true);
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
                return transaction.Abort(false);
            }

            var updated = await gateways.ReplaceOneAsync(transaction.Session,
                PrivateAccessGatewayFilters.Current(
                    current.TenantId, current.DeploymentTargetId,
                    current.Id, current.Generation, current.Revision),
                replacement, cancellationToken: ct);
            if (updated.MatchedCount != 1)
                return transaction.Abort(false);

            var owner = await owners.Find(transaction.Session,
                    PrivateAccessBrokerOwnerFilters.ByGateway(identity.GatewayId))
                .FirstOrDefaultAsync(ct);
            if (owner is null || owner.GatewayGeneration != current.Generation ||
                owner.GatewayRevision != current.Revision)
                return transaction.Abort(false);
            var ownerUpdated = await owners.ReplaceOneAsync(transaction.Session,
                PrivateAccessBrokerOwnerFilters.Current(owner),
                owner.Reset(replacement.Generation, replacement.Revision),
                cancellationToken: ct);
            return ownerUpdated.MatchedCount == 1
                ? transaction.Commit(true)
                : transaction.Abort(false);
        }, cancellationToken);
    }

    public async Task<bool> RemoveAsync(
        PrivateAccessGatewayIdentity identity,
        CancellationToken cancellationToken = default)
    {
        return await transactions.InvokeOutcomeAsync<bool>(async (transaction, ct) =>
        {
            var result = await gateways.DeleteOneAsync(transaction.Session,
                PrivateAccessGatewayFilters.Current(
                    identity.TenantId, identity.DeploymentTargetId,
                    identity.GatewayId, identity.Generation, identity.Revision),
                cancellationToken: ct);
            if (result.DeletedCount != 1)
                return transaction.Abort(false);
            var owner = await owners.Find(transaction.Session,
                    PrivateAccessBrokerOwnerFilters.ByGateway(identity.GatewayId))
                .FirstOrDefaultAsync(ct);
            if (owner is null || owner.GatewayGeneration != identity.Generation ||
                owner.GatewayRevision != identity.Revision)
                return transaction.Abort(false);
            var reset = await owners.ReplaceOneAsync(transaction.Session,
                PrivateAccessBrokerOwnerFilters.Current(owner),
                owner.Reset(identity.Generation, identity.Revision),
                cancellationToken: ct);
            if (reset.MatchedCount != 1)
                return transaction.Abort(false);
            var reference = await targetReferences(
                GatewayOwner(identity), [], [identity.DeploymentTargetId],
                transaction.Session, ct);
            return reference is DeploymentTargetReferencesChanged
                ? transaction.Commit(true)
                : transaction.Abort(false);
        }, cancellationToken);
    }

    private static DeploymentTargetReferenceOwner GatewayOwner(
        PrivateAccessGatewayIdentity identity) => new(
        DeploymentTargetReferenceOwnerTypes.PrivateAccessGateway,
        identity.GatewayId);

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
