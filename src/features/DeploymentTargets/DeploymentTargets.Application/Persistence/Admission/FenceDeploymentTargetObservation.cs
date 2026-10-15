using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

internal sealed class MongoFenceDeploymentTargetObservation(IMongoCollection<DeploymentTarget> targets)
{
    public async Task<FenceDeploymentTargetObservationOutcome> InvokeAsync(
        DeploymentTargetObservationFenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
            throw new InvalidOperationException(
                "Deployment-target observation fencing requires the owner's Mongo transaction.");
        if (!ObjectId.TryParse(request.DeploymentTargetId, out var id) || request.ExpectedConnectionVersion < 1)
            throw new ArgumentException("A valid target and positive connection version are required.",
                nameof(request));
        var filter =
            DeploymentTargetObservationFilters.CurrentEnabledConnection(id.ToString(),
                request.ExpectedConnectionVersion);
        var result = await targets.UpdateOneAsync(session,
            filter,
            Builders<DeploymentTarget>.Update.Inc(target => target.ObservationFence, 1),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1
            ? new DeploymentTargetObservationFenced()
            : new DeploymentTargetObservationFenceRejected();
    }
}
