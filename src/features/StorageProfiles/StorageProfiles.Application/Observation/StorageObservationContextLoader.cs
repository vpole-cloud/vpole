using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Observation;

public sealed class StorageObservationContextLoader(
    LookupDeploymentTarget lookup
)
{
    public async Task<StorageObservationContext?> LoadAsync(string targetId, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(targetId, out var id))
            return null;
        var outcome = await lookup(id.ToString(), ct);
        return outcome.Value is DeploymentTargetLookupDto
        {
            Enabled: true, DeletionRequested: false, ConnectionVersion: > 0,
        } target
            ? new StorageObservationContext(target.Id, target.ConnectionVersion)
            : null;
    }

    public async Task<bool> IsCurrentAsync(StorageObservationContext context, CancellationToken ct = default) =>
        await LoadAsync(context.DeploymentTargetId, ct) == context;
}
