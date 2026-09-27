using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

internal sealed class MongoFenceDeploymentTargetWorkloadConfigurations(
    IMongoCollection<DeploymentTarget> deploymentTargets)
{
    public async Task<FenceDeploymentTargetWorkloadConfigurationsOutcome> InvokeAsync(
        IReadOnlyList<DeploymentTargetWorkloadConfigurationFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        EnsureTransaction(session);
        if (requests.Count == 0)
            return new DeploymentTargetWorkloadConfigurationsFenced();

        var distinctRequests = requests
            .DistinctBy(request => request.DeploymentTargetId, StringComparer.Ordinal)
            .ToArray();
        if (distinctRequests.Length != requests.Count ||
            distinctRequests.Any(request => !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
                request.ExpectedWorkloadConfigurationRevision < 1))
        {
            throw new ArgumentException(
                "Unique valid deployment targets and workload configuration revisions are required.",
                nameof(requests));
        }

        var update = await deploymentTargets.UpdateManyAsync(
            session,
            DeploymentTargetFilters.FenceableWorkloadConfigurations(distinctRequests),
            Builders<DeploymentTarget>.Update.Inc(target => target.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == distinctRequests.Length
            ? new DeploymentTargetWorkloadConfigurationsFenced()
            : new DeploymentTargetWorkloadConfigurationFenceRejected(requests);
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Deployment-target reference admission must run inside the reference owner's MongoDB transaction.");
        }
    }
}
