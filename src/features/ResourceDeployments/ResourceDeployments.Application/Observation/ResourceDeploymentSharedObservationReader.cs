using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Observation;

public closed record ObservationRead;

public sealed record LiveObservationRead(
    ResourceDeploymentObservationContext? Context,
    long? ObservationVersion) : ObservationRead;

public sealed record SharedObservationRead(ResourceDeploymentSharedObservation Snapshot) : ObservationRead;

public sealed record UnavailableObservationRead(
    string Reason,
    long? ObservationVersion) : ObservationRead;

public sealed class ResourceDeploymentSharedObservationReader(
    IResourceDeploymentObservationStore store,
    ResourceDeploymentObservationContextLoader contexts,
    ResourceDeploymentObservationOptions options,
    ResourceDeploymentObservationMetrics? metrics = null)
{
    public async Task<ObservationRead> BeginAsync(ResourceDeployment deployment, CancellationToken ct)
    {
        // Pin BEFORE a live native GET. A later notification invalidates the client read's epoch;
        // it must never label older evidence with a version obtained after that evidence.
        var control = await store.LoadControlAsync(new(deployment.TenantId, deployment.Id), ct);
        var version = control?.ObservationVersion > 0 ? control.ObservationVersion : (long?)null;
        if (!contexts.SupportsWatch(deployment.ResourceType))
        {
            metrics?.RecordRead(ObservationReadOutcome.Live);
            return new LiveObservationRead(null, version);
        }

        var context = await contexts.FromAsync(deployment, ct);
        if (context is null)
        {
            metrics?.RecordRead(ObservationReadOutcome.Unavailable);
            return new UnavailableObservationRead("TargetUnavailable", version);
        }

        if (options.Enabled && deployment.DesiredOperation == ResourceDeploymentOperation.Apply &&
            deployment.State is not DeletedResourceDeploymentState && await store.LoadFreshSnapshotAsync(context, ct) is
            { } snapshot)
        {
            metrics?.RecordRead(ObservationReadOutcome.Shared);
            return new SharedObservationRead(snapshot);
        }

        metrics?.RecordRead(ObservationReadOutcome.Live);
        return new LiveObservationRead(context, version);
    }

    public Task<bool> IsCurrentAsync(ResourceDeployment deployment,
        ResourceDeploymentObservationContext? context,
        CancellationToken ct) =>
        contexts.IsCurrentAsync(deployment, context, ct);
}
