using Pole.Observation;
using System.Net;
using k8s.Autorest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Diagnostics;

public sealed class RuntimeDiagnosticsOptions
{
    public const string SectionName = "RuntimeDiagnostics";

    public int MaximumBytes
    {
        get;
        init;
    } = 1024 * 1024;

    public int LifetimeSeconds
    {
        get;
        init;
    } = 120;

    public int OwnershipCheckSeconds
    {
        get;
        init;
    } = 5;

    public bool Valid => MaximumBytes is >= 4096 and <= 4 * 1024 * 1024 && LifetimeSeconds is >= 10 and <= 300 &&
                         OwnershipCheckSeconds is >= 1 and <= 10;
}

public sealed class RuntimeLogCapacity
{
    internal SemaphoreSlim Slots
    {
        get;
    } = new(4);
}

public sealed class PreparedRuntimeLogSession(
    ResourceDeployment deployment,
    RuntimeLogSource source,
    CancellationTokenSource lifetime,
    bool follow,
    Action release) : IAsyncDisposable
{
    public ResourceDeployment Deployment
    {
        get;
    } = deployment;

    public RuntimeLogSource Source
    {
        get;
    } = source;

    public CancellationTokenSource Lifetime
    {
        get;
    } = lifetime;

    public bool Follow
    {
        get;
    } = follow;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Lifetime.CancelAsync();
            await Source.DisposeAsync();
        }
        finally
        {
            Lifetime.Dispose();
            release();
        }
    }
}

public closed record PrepareRuntimeLogsResult;

public sealed record PreparedRuntimeLogs(PreparedRuntimeLogSession Session) : PrepareRuntimeLogsResult;

public sealed record RuntimeLogsUnavailable(string Reason) : PrepareRuntimeLogsResult;

public static class RuntimeDiagnosticFailures
{
    public static string? Reason(Exception exception) => exception switch
    {
        DeploymentTargetUnavailableException => "TargetUnavailable",
        HttpOperationException { Response.StatusCode: HttpStatusCode.NotFound } => "InstanceRemoved",
        HttpOperationException
        {
            Response.StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
        } => "Forbidden",
        HttpOperationException { Response.StatusCode: HttpStatusCode.BadRequest } => "LogsUnavailable",
        HttpOperationException or HttpRequestException or IOException => "TargetUnavailable",
        _ => null
    };
}

public sealed class ResourceDeploymentDiagnostics(
    ResourceDeploymentOperationsPersistence persistence,
    ResourceDeploymentObserverCatalog catalog,
    IServiceProvider services,
    TimeProvider clock,
    IHostApplicationLifetime host,
    RuntimeLogCapacity capacity,
    RuntimeDiagnosticsOptions options,
    ResourceDeploymentSharedObservationReader shared,
    IObservationReadBudget reads)
{
    public RuntimeDiagnosticsOptions Options => options;
    public CancellationToken Shutdown => host.ApplicationStopping;

    private IResourceDeploymentDiagnosticsProvider? Provider(ResourceDeployment deployment) =>
        catalog.Find(deployment.ResourceType)?.DiagnosticsType is { } type
            ? (IResourceDeploymentDiagnosticsProvider)services.GetRequiredService(type)
            : null;

    public async Task<bool> IsCurrentAsync(ResourceDeployment deployment, CancellationToken ct)
    {
        var current = await persistence.LoadAsync(deployment.TenantId, deployment.Id, ct);
        return current is not null && current.Revision == deployment.Revision &&
               current.DesiredGeneration == deployment.DesiredGeneration &&
               current.ResourceType == deployment.ResourceType && current.ResourceId == deployment.ResourceId &&
               current.ResourceGroupId == deployment.ResourceGroupId &&
               current.IsolationScopeId == deployment.IsolationScopeId &&
               current.RuntimeCellId == deployment.RuntimeCellId &&
               current.ResolvedPlacement.DeploymentTargetId == deployment.ResolvedPlacement.DeploymentTargetId &&
               current.ResolvedPlacement.KubernetesNamespace == deployment.ResolvedPlacement.KubernetesNamespace;
    }

    public async Task<ResourceDeploymentDiagnosticsDto> DiscoverAsync(ResourceDeployment deployment,
        CancellationToken ct)
    {
        var read = await shared.BeginAsync(deployment, ct);
        if (read is SharedObservationRead { Snapshot.Diagnostics: { } diagnosticsSnapshot } cached)
            return new(deployment.Id,
                    deployment.ResourceType,
                    deployment.ResourceId,
                    deployment.ResolvedPlacement.DeploymentTargetId,
                    deployment.Revision,
                    deployment.DesiredGeneration,
                    cached.Snapshot.CheckedAtUtc,
                    diagnosticsSnapshot)
            {
                ObservationVersion = cached.Snapshot.ObservationVersion
            };
        var version = read switch
        {
            LiveObservationRead live => live.ObservationVersion,
            UnavailableObservationRead unavailable => unavailable.ObservationVersion,
            SharedObservationRead sharedRead => sharedRead.Snapshot.ObservationVersion
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Shutdown);
        WorkloadDiagnostics diagnostics;
        try
        {
            diagnostics = read is UnavailableObservationRead unavailable
                ? new UnavailableWorkloadDiagnostics(unavailable.Reason)
                : await reads.ReadAsync(async token => Provider(deployment) is { } provider
                        ? await provider.DiscoverAsync(deployment.ToReconciliationSnapshot(), token)
                        : new UnavailableWorkloadDiagnostics("Unsupported"),
                    linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !Shutdown.IsCancellationRequested)
        {
            diagnostics = new UnavailableWorkloadDiagnostics("Timeout");
        }
        catch (Exception exception) when (RuntimeDiagnosticFailures.Reason(exception) is { })
        {
            diagnostics = new UnavailableWorkloadDiagnostics(RuntimeDiagnosticFailures.Reason(exception)!);
        }

        if (!await shared.IsCurrentAsync(deployment, (read as LiveObservationRead)?.Context, ct))
            diagnostics = new UnavailableWorkloadDiagnostics("Superseded");
        return new(deployment.Id,
                deployment.ResourceType,
                deployment.ResourceId,
                deployment.ResolvedPlacement.DeploymentTargetId,
                deployment.Revision,
                deployment.DesiredGeneration,
                clock.GetUtcNow().UtcDateTime,
                diagnostics)
        {
            ObservationVersion = version
        };
    }

    public async Task<PrepareRuntimeLogsResult> OpenAsync(ResourceDeployment deployment,
        RuntimeLogSelection selection,
        CancellationToken ct)
    {
        var provider = Provider(deployment);
        if (provider is null)
            return new RuntimeLogsUnavailable("Unsupported");
        if (!await capacity.Slots.WaitAsync(TimeSpan.Zero, ct))
            return new RuntimeLogsUnavailable("CapacityBusy");
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, Shutdown);
        lifetime.CancelAfter(TimeSpan.FromSeconds(options.LifetimeSeconds));
        RuntimeLogSource? source = null;
        var transferred = false;
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            opening.CancelAfter(TimeSpan.FromSeconds(15));
            RuntimeLogOpenResult opened;
            try
            {
                opened = await provider.OpenLogsAsync(deployment.ToReconciliationSnapshot(), selection, opening.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !Shutdown.IsCancellationRequested)
            {
                return new RuntimeLogsUnavailable("Timeout");
            }
            catch (Exception exception) when (RuntimeDiagnosticFailures.Reason(exception) is { })
            {
                return new RuntimeLogsUnavailable(RuntimeDiagnosticFailures.Reason(exception)!);
            }

            switch (opened)
            {
                case UnavailableRuntimeLog unavailable:
                    return new RuntimeLogsUnavailable(unavailable.Reason);
                case OpenRuntimeLog ready:
                    source = ready.Source;
                    break;
            }

            if (!await IsCurrentAsync(deployment, ct))
                return new RuntimeLogsUnavailable("Superseded");
            var session = new PreparedRuntimeLogSession(deployment,
                source!,
                lifetime,
                selection.Follow,
                () => capacity.Slots.Release());
            transferred = true;
            return new PreparedRuntimeLogs(session);
        }
        finally
        {
            if (!transferred)
            {
                try
                {
                    if (source is not null)
                        await source.DisposeAsync();
                }
                finally
                {
                    lifetime.Dispose();
                    capacity.Slots.Release();
                }
            }
        }
    }
}
