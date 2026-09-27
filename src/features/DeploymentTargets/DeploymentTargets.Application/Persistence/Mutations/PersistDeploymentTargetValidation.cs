using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Mutations;

public union PersistDeploymentTargetValidationOutcome(
    DeploymentTarget,
    DeploymentTargetRevisionConflict);

public delegate Task<PersistDeploymentTargetValidationOutcome>
    PersistDeploymentTargetValidation(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistDeploymentTargetValidation(
    IMongoCollection<DeploymentTarget> deploymentTargets)
{
    public async Task<PersistDeploymentTargetValidationOutcome> InvokeAsync(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.Id, replacement.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The current and replacement deployment targets must have the same ID.",
                nameof(replacement));
        }

        var persisted = await deploymentTargets.FindOneAndUpdateAsync(
            DeploymentTargetFilters.AtRevisionAndConnectionVersion(current),
            Builders<DeploymentTarget>.Update.Combine(
                Builders<DeploymentTarget>.Update.Set(item => item.Health, replacement.Health),
                Builders<DeploymentTarget>.Update.Set(item => item.Capabilities, replacement.Capabilities)),
            new FindOneAndUpdateOptions<DeploymentTarget>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        return persisted is null
            ? new DeploymentTargetRevisionConflict(current.Id)
            : persisted;
    }
}
