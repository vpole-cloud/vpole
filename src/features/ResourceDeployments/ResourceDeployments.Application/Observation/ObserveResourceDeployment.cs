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
    ResourceDeploymentOperationsPersistence persistence,
    ResourceDeploymentObserverCatalog catalog,
    IServiceProvider services,
    TimeProvider clock)
{
    // Across tenant observation requests, bound cluster traffic independently of pagination.
    private static readonly SemaphoreSlim Capacity = new(4);

    public async Task<ResourceDeploymentObservationDto> InvokeAsync(ResourceDeployment deployment, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        WorkloadObservation observation;
        var acquired = false;
        try
        {
            await Capacity.WaitAsync(timeout.Token);
            acquired = true;
            var descriptor = catalog.Find(deployment.ResourceType);
            observation = descriptor is null ? new UnavailableWorkloadObservation("Unsupported") :
                await ((IResourceDeploymentObserver)services.GetRequiredService(descriptor.ObserverType))
                    .ObserveAsync(deployment.ToReconciliationSnapshot(), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { observation = new UnavailableWorkloadObservation("Timeout"); }
        catch (DeploymentTargetUnavailableException) { observation = new UnavailableWorkloadObservation("TargetUnavailable"); }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound) { observation = new UnavailableWorkloadObservation("NotFound"); }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { observation = new UnavailableWorkloadObservation("Forbidden"); }
        catch (HttpOperationException) { observation = new UnavailableWorkloadObservation("ClusterUnavailable"); }
        catch (HttpRequestException) { observation = new UnavailableWorkloadObservation("ClusterUnavailable"); }
        finally { if (acquired) Capacity.Release(); }
        var current = await persistence.LoadAsync(deployment.TenantId, deployment.Id, cancellationToken);
        if (current?.Revision != deployment.Revision || current.DesiredGeneration != deployment.DesiredGeneration)
            observation = new UnavailableWorkloadObservation("Superseded");
        return new ResourceDeploymentObservationDto(deployment.Id, deployment.Revision, deployment.DesiredGeneration,
            clock.GetUtcNow().UtcDateTime, observation);
    }
}
