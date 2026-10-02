using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal sealed class MongoPrivateAccessBrokerOwnership(
    IMongoCollection<PrivateAccessBrokerOwner> owners,
    IMongoCollection<PrivateAccessGateway> gateways,
    MongoRunTransaction transactions)
{
    private const int LeaseSeconds = 10;

    public Task<ClaimPrivateAccessBrokerOwnerOutcome> ClaimAsync(
        PrivateAccessGatewayIdentity gateway,
        string instanceId,
        string internalOrigin,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        PrivateAccessBrokerOwner.ValidateOrigin(internalOrigin);
        return transactions.InvokeOutcomeAsync<ClaimPrivateAccessBrokerOwnerOutcome>(
            async (transaction, ct) =>
            {
                var registration = await gateways.Find(transaction.Session,
                        PrivateAccessGatewayFilters.Current(
                            gateway.TenantId, gateway.DeploymentTargetId,
                            gateway.GatewayId, gateway.Generation, gateway.Revision))
                    .FirstOrDefaultAsync(ct);
                if (registration is null)
                    return transaction.Abort(new ClaimPrivateAccessBrokerOwnerOutcome(
                        new PrivateAccessBrokerGatewayUnavailable()));

                var owner = await owners.Find(transaction.Session,
                        PrivateAccessBrokerOwnerFilters.ByGateway(gateway.GatewayId))
                    .FirstOrDefaultAsync(ct);
                if (owner is null || owner.TenantId != gateway.TenantId ||
                    owner.DeploymentTargetId != gateway.DeploymentTargetId ||
                    owner.GatewayGeneration != gateway.Generation ||
                    owner.GatewayRevision != gateway.Revision)
                    return transaction.Abort(new ClaimPrivateAccessBrokerOwnerOutcome(
                        new PrivateAccessBrokerGatewayUnavailable()));

                var claimed = await owners.FindOneAndUpdateAsync(transaction.Session,
                    PrivateAccessBrokerOwnerFilters.Claimable(owner, instanceId),
                    LeaseUpdate(instanceId, internalOrigin, incrementEpoch: true),
                    new FindOneAndUpdateOptions<PrivateAccessBrokerOwner>
                    {
                        ReturnDocument = ReturnDocument.After
                    }, ct);
                return claimed is not null
                    ? transaction.Commit(new ClaimPrivateAccessBrokerOwnerOutcome(ToLease(claimed)))
                    : transaction.Abort(new ClaimPrivateAccessBrokerOwnerOutcome(
                        new PrivateAccessBrokerOwnerBusy()));
            }, cancellationToken);
    }

    public async Task<bool> RenewAsync(
        PrivateAccessBrokerOwnerLease lease,
        CancellationToken cancellationToken = default)
    {
        var updated = await owners.UpdateOneAsync(
            PrivateAccessBrokerOwnerFilters.ActiveLease(lease),
            LeaseUpdate(lease.InstanceId, lease.InternalOrigin, incrementEpoch: false),
            cancellationToken: cancellationToken);
        return updated.MatchedCount == 1;
    }

    public async Task ReleaseAsync(
        PrivateAccessBrokerOwnerLease lease,
        CancellationToken cancellationToken = default)
    {
        await owners.UpdateOneAsync(
            PrivateAccessBrokerOwnerFilters.Lease(lease),
            Builders<PrivateAccessBrokerOwner>.Update
                .Set(item => item.InstanceId, null)
                .Set(item => item.InternalOrigin, null)
                .Set(item => item.LeaseExpiresAtUtc, DateTime.UnixEpoch),
            cancellationToken: cancellationToken);
    }

    public async Task<PrivateAccessBrokerOwnerLease?> LoadAsync(
        string tenantId,
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var gatewayId = PrivateAccessGatewayIds.StableId(tenantId, deploymentTargetId);
        var owner = await owners.Find(PrivateAccessBrokerOwnerFilters.ActiveForGateway(
                gatewayId, tenantId, deploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
        return owner is null ? null : ToLease(owner);
    }

    private static PrivateAccessBrokerOwnerLease ToLease(PrivateAccessBrokerOwner owner) => new(
        new PrivateAccessGatewayIdentity(owner.TenantId, owner.DeploymentTargetId,
            owner.Id, owner.GatewayGeneration, owner.GatewayRevision),
        owner.InstanceId!, owner.InternalOrigin!, owner.Epoch,
        owner.LeaseExpiresAtUtc);

    private static UpdateDefinition<PrivateAccessBrokerOwner> LeaseUpdate(
        string instanceId,
        string internalOrigin,
        bool incrementEpoch)
    {
        var fields = new BsonDocument
        {
            { nameof(PrivateAccessBrokerOwner.InstanceId), instanceId },
            { nameof(PrivateAccessBrokerOwner.InternalOrigin), internalOrigin },
            {
                nameof(PrivateAccessBrokerOwner.LeaseExpiresAtUtc),
                new BsonDocument("$dateAdd", new BsonDocument
                {
                    { "startDate", "$$NOW" },
                    { "unit", "second" },
                    { "amount", LeaseSeconds }
                })
            }
        };
        if (incrementEpoch)
            fields.Add(nameof(PrivateAccessBrokerOwner.Epoch),
                new BsonDocument("$add", new BsonArray { "$Epoch", 1 }));
        return Builders<PrivateAccessBrokerOwner>.Update.Pipeline(
            PipelineDefinition<PrivateAccessBrokerOwner, PrivateAccessBrokerOwner>.Create(
                [new BsonDocument("$set", fields)]));
    }
}

internal static class PrivateAccessBrokerOwnerFilters
{
    public static FilterDefinition<PrivateAccessBrokerOwner> ByGateway(string gatewayId) =>
        Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.Id, gatewayId);

    public static FilterDefinition<PrivateAccessBrokerOwner> Current(
        PrivateAccessBrokerOwner owner) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(
            ByGateway(owner.Id),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.Epoch, owner.Epoch),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.GatewayGeneration, owner.GatewayGeneration),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.GatewayRevision, owner.GatewayRevision));

    public static FilterDefinition<PrivateAccessBrokerOwner> Claimable(
        PrivateAccessBrokerOwner owner,
        string instanceId) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(
            Current(owner),
            Builders<PrivateAccessBrokerOwner>.Filter.Or(
                Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                    item => item.InstanceId, instanceId),
                ServerExpiry("$lte")));

    public static FilterDefinition<PrivateAccessBrokerOwner> Lease(
        PrivateAccessBrokerOwnerLease lease) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(
            ByGateway(lease.Gateway.GatewayId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.TenantId, lease.Gateway.TenantId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.DeploymentTargetId, lease.Gateway.DeploymentTargetId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.GatewayGeneration, lease.Gateway.Generation),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.GatewayRevision, lease.Gateway.Revision),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.InstanceId, lease.InstanceId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.Epoch, lease.Epoch));

    public static FilterDefinition<PrivateAccessBrokerOwner> ActiveLease(
        PrivateAccessBrokerOwnerLease lease) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(Lease(lease), ServerExpiry("$gt"));

    public static FilterDefinition<PrivateAccessBrokerOwner> ActiveForGateway(
        string gatewayId,
        string tenantId,
        string targetId) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(
            ByGateway(gatewayId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.TenantId, tenantId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.DeploymentTargetId, targetId),
            Builders<PrivateAccessBrokerOwner>.Filter.Ne(item => item.InstanceId, null),
            Builders<PrivateAccessBrokerOwner>.Filter.Ne(item => item.InternalOrigin, null),
            ServerExpiry("$gt"));

    private static FilterDefinition<PrivateAccessBrokerOwner> ServerExpiry(string comparison) =>
        new BsonDocument("$expr", new BsonDocument(comparison,
            new BsonArray { "$LeaseExpiresAtUtc", "$$NOW" }));
}
