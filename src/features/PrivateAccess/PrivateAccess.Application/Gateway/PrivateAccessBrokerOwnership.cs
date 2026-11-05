using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Application.Catalog;
using Pole.Features.PrivateAccess.Application.Admission;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Gateway;

internal sealed class MongoPrivateAccessBrokerOwnership(
    IMongoCollection<PrivateAccessBrokerOwner> owners,
    IMongoCollection<PrivateAccessGateway> gateways,
    MongoRunTransaction transactions)
{
    private const int LeaseSeconds = 10;

    public Task<ClaimPrivateAccessBrokerOwnerOutcome> ClaimAsync(
        ClaimPrivateAccessBrokerOwnerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);
        PrivateAccessBrokerOwner.ValidateOrigin(request.InternalOrigin);
        return transactions.InvokeOutcomeAsync<ClaimPrivateAccessBrokerOwnerOutcome>(
            async (transaction, ct) =>
            {
                var registration = await gateways.Find(transaction.Session,
                        PrivateAccessGatewayFilters.Current(
                            request.Gateway.TenantId, request.Gateway.DeploymentTargetId,
                            request.Gateway.GatewayId, request.Gateway.Generation, request.Gateway.Revision))
                    .FirstOrDefaultAsync(ct);
                if (registration is null)
                    return transaction.Abort(new ClaimPrivateAccessBrokerOwnerOutcome(
                        new PrivateAccessBrokerGatewayUnavailable()));

                var owner = await owners.Find(transaction.Session,
                        PrivateAccessBrokerOwnerFilters.ByGateway(request.Gateway.GatewayId))
                    .FirstOrDefaultAsync(ct);
                if (owner is null || owner.TenantId != request.Gateway.TenantId ||
                    owner.DeploymentTargetId != request.Gateway.DeploymentTargetId ||
                    owner.GatewayGeneration != request.Gateway.Generation ||
                    owner.GatewayRevision != request.Gateway.Revision)
                    return transaction.Abort(new ClaimPrivateAccessBrokerOwnerOutcome(
                        new PrivateAccessBrokerGatewayUnavailable()));

                var claimed = await owners.FindOneAndUpdateAsync(transaction.Session,
                    PrivateAccessBrokerOwnerFilters.Claimable(owner, request.InstanceId),
                    LeaseUpdate(request.InstanceId, request.InternalOrigin, incrementEpoch: true),
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
