using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Connectors;

public sealed record DeploymentTargetConnectorList(IReadOnlyList<DeploymentTargetConnectorDto> Items);

public union LoadDeploymentTargetConnectorListOutcome(
    DeploymentTargetConnectorList,
    DeploymentTargetNotFound);

public union LoadDeploymentTargetConnectorStatusOutcome(
    DeploymentTargetConnectorDto,
    DeploymentTargetConnectorNotFound);

public delegate Task<LoadDeploymentTargetConnectorListOutcome> LoadDeploymentTargetConnectorList(
    string targetId,
    CancellationToken cancellationToken = default);

public delegate Task<LoadDeploymentTargetConnectorStatusOutcome> LoadDeploymentTargetConnectorStatus(
    string targetId,
    string connectorId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDeploymentTargetConnectorStatus(
    IMongoCollection<DeploymentTarget> targets,
    IMongoCollection<DeploymentTargetConnector> connectors,
    TimeProvider clock)
{
    public async Task<LoadDeploymentTargetConnectorListOutcome> ListAsync(
        string targetId,
        CancellationToken cancellationToken = default)
    {
        var target = await targets
            .Find(DeploymentTargetFilters.ById(targetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null)
            return new DeploymentTargetNotFound(targetId);

        var found = await connectors
            .Find(DeploymentTargetConnectorFilters.ByTarget(targetId))
            .SortBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        return new DeploymentTargetConnectorList(found.Select(item => item.ToDto(now)).ToArray());
    }

    public async Task<LoadDeploymentTargetConnectorStatusOutcome> GetAsync(
        string targetId,
        string connectorId,
        CancellationToken cancellationToken = default)
    {
        var connector = await connectors
            .Find(DeploymentTargetConnectorFilters.ByIdAndTarget(connectorId, targetId))
            .FirstOrDefaultAsync(cancellationToken);
        return connector is null
            ? new DeploymentTargetConnectorNotFound(connectorId)
            : connector.ToDto(clock.GetUtcNow().UtcDateTime);
    }
}
