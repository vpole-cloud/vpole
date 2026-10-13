using System.Net;
using k8s.Autorest;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Observation;

public sealed class ObserveResourceDeployment(
    ResourceDeploymentObserverCatalog catalog,
    IServiceProvider services,
    TimeProvider clock,
    ResourceDeploymentSharedObservationReader shared,
    IResourceDeploymentObservationReadBudget reads)
{
    public async Task<ResourceDeploymentObservationDto> InvokeAsync(ResourceDeployment deployment, CancellationToken cancellationToken)
    {
        var read = await shared.BeginAsync(deployment, cancellationToken);
        if (read is SharedObservationRead cached)
            return new(deployment.Id, deployment.Revision, deployment.DesiredGeneration,
                cached.Snapshot.CheckedAtUtc, cached.Snapshot.Observation)
            { ObservationVersion = cached.Snapshot.ObservationVersion };
        var version = read switch
        {
            LiveObservationRead live => live.ObservationVersion,
            UnavailableObservationRead unavailable => unavailable.ObservationVersion,
            SharedObservationRead => throw new InvalidOperationException("Shared observation was already returned.")
        };
        WorkloadObservation observation;
        try
        {
            observation = read is UnavailableObservationRead unavailable ? new UnavailableWorkloadObservation(unavailable.Reason) :
                await reads.ReadAsync(async token =>
                {
                    var descriptor = catalog.Find(deployment.ResourceType);
                    return descriptor is null ? new UnavailableWorkloadObservation("Unsupported") :
                        await ((IResourceDeploymentObserver)services.GetRequiredService(descriptor.ObserverType))
                            .ObserveAsync(deployment.ToReconciliationSnapshot(), token);
                }, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { observation = new UnavailableWorkloadObservation("Timeout"); }
        catch (DeploymentTargetUnavailableException) { observation = new UnavailableWorkloadObservation("TargetUnavailable"); }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound) { observation = new UnavailableWorkloadObservation("NotFound"); }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { observation = new UnavailableWorkloadObservation("Forbidden"); }
        catch (HttpOperationException) { observation = new UnavailableWorkloadObservation("ClusterUnavailable"); }
        catch (HttpRequestException) { observation = new UnavailableWorkloadObservation("ClusterUnavailable"); }
        if (!await shared.IsCurrentAsync(deployment, (read as LiveObservationRead)?.Context, cancellationToken))
            observation = new UnavailableWorkloadObservation("Superseded");
        return new ResourceDeploymentObservationDto(deployment.Id, deployment.Revision, deployment.DesiredGeneration,
            clock.GetUtcNow().UtcDateTime, observation)
        { ObservationVersion = version };
    }
}
