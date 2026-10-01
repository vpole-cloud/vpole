using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Connectors;

public sealed record DeploymentTargetConnectorRevoked(
    string TargetId,
    string ConnectorId,
    long Revision);

public union RevokeDeploymentTargetConnectorPersistenceOutcome(
    DeploymentTargetConnectorRevoked,
    DeploymentTargetConnectorNotFound,
    DeploymentTargetConnectorRevisionConflict);

public delegate Task<RevokeDeploymentTargetConnectorPersistenceOutcome> RevokeDeploymentTargetConnector(
    string targetId,
    string connectorId,
    long expectedConnectorRevision,
    CancellationToken cancellationToken = default);

internal sealed class MongoRevokeDeploymentTargetConnector(
    IMongoCollection<DeploymentTargetConnector> connectors,
    TimeProvider clock)
{
    public async Task<RevokeDeploymentTargetConnectorPersistenceOutcome> InvokeAsync(
        string targetId,
        string connectorId,
        long expectedConnectorRevision,
        CancellationToken cancellationToken = default)
    {
        var current = await connectors
            .Find(DeploymentTargetConnectorFilters.ByIdAndTarget(connectorId, targetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new DeploymentTargetConnectorNotFound(connectorId);
        if (current.Revision != expectedConnectorRevision)
            return new DeploymentTargetConnectorRevisionConflict(connectorId);
        if (current.State is RevokedDeploymentTargetConnectorState)
            return new DeploymentTargetConnectorRevoked(current.TargetId, current.Id, current.Revision);

        var revoked = current.Revoke(clock.GetUtcNow().UtcDateTime);
        var updated = await connectors.FindOneAndUpdateAsync(
            DeploymentTargetConnectorFilters.ActiveAtRevision(current),
            Builders<DeploymentTargetConnector>.Update
                .Set(item => item.State, revoked.State)
                .Set(item => item.Revision, revoked.Revision)
                .Set(item => item.UpdatedAtUtc, revoked.UpdatedAtUtc),
            new FindOneAndUpdateOptions<DeploymentTargetConnector>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        return updated is null
            ? new DeploymentTargetConnectorRevisionConflict(connectorId)
            : new DeploymentTargetConnectorRevoked(updated.TargetId, updated.Id, updated.Revision);
    }
}
