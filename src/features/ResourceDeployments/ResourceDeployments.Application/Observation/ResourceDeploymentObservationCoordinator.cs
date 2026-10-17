using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Observation;
using SampleOutcome = Pole.Observation.ObservationSampleMetric;

namespace Pole.Features.ResourceDeployments.Application.Observation;

public sealed class ResourceDeploymentObservationWakeup : ObservationWakeup;

public sealed class ResourceDeploymentObservationCoordinator(
    IServiceScopeFactory scopes,
    ResourceDeploymentObservationOptions options,
    ResourceDeploymentObservationWakeup wakeup,
    TimeProvider clock,
    ILogger<ResourceDeploymentObservationCoordinator> logger,
    ResourceDeploymentObservationMetrics? metrics = null)
    : ObservationCoordinator<ObservationIdentity, ResourceDeploymentObservationContext, ObservationOwnership>(
        scopes,
        options.Enabled,
        options.MaximumSources,
        options.ScanBatchSize,
        wakeup,
        clock,
        logger)
{
    protected override IObservationCoordination<ObservationIdentity, ResourceDeploymentObservationContext,
            ObservationOwnership>
        CreateAdapter(IServiceProvider provider) => new Adapter(provider, clock, metrics);

    private static bool SameContext(ResourceDeploymentObservationContext? current,
        ResourceDeploymentObservationContext expected) =>
        current is not null && current.DeploymentRevision == expected.DeploymentRevision &&
        current.TargetConnectionVersion == expected.TargetConnectionVersion &&
        current.Deployment.Id == expected.Deployment.Id &&
        current.Deployment.TenantId == expected.Deployment.TenantId &&
        current.Deployment.ResourceType == expected.Deployment.ResourceType &&
        current.Deployment.ResourceId == expected.Deployment.ResourceId &&
        current.Deployment.ResourceGroupId == expected.Deployment.ResourceGroupId &&
        current.Deployment.IsolationScopeId == expected.Deployment.IsolationScopeId &&
        current.Deployment.RuntimeCellId == expected.Deployment.RuntimeCellId &&
        current.Deployment.ResourceRevision == expected.Deployment.ResourceRevision &&
        current.Deployment.DesiredGeneration == expected.Deployment.DesiredGeneration &&
        current.Deployment.Placement.DeploymentTargetId == expected.Deployment.Placement.DeploymentTargetId &&
        current.Deployment.Placement.TargetWorkloadConfigurationRevision ==
        expected.Deployment.Placement.TargetWorkloadConfigurationRevision &&
        current.Deployment.Placement.KubernetesNamespace == expected.Deployment.Placement.KubernetesNamespace;

    private sealed class Adapter(
        IServiceProvider provider,
        TimeProvider clock,
        ResourceDeploymentObservationMetrics? metrics)
        : IObservationCoordination<ObservationIdentity, ResourceDeploymentObservationContext, ObservationOwnership>
    {
        private readonly IResourceDeploymentObservationStore store =
            provider.GetRequiredService<IResourceDeploymentObservationStore>();

        private readonly ResourceDeploymentObservationContextLoader contexts =
            provider.GetRequiredService<ResourceDeploymentObservationContextLoader>();

        public Task ReapAsync(int limit, CancellationToken ct) => store.ReapExpiredDemandsAsync(limit, ct);

        public async Task<ObservationPage<ObservationIdentity>> ScanAsync(string? cursor,
            int limit,
            CancellationToken ct)
        {
            var page = await store.ScanDemandsAsync(cursor, limit, ct);
            return new(page.Items, page.NextCursor);
        }

        public async Task<bool> IsCurrentAsync(ObservationIdentity key,
            ResourceDeploymentObservationContext context,
            ObservationOwnership owner,
            CancellationToken ct) =>
            (await store.LoadControlAsync(key, ct))?.Epoch == owner.Lease.Epoch &&
            SameContext(await contexts.LoadWatchAsync(key, ct), context);

        public Task<ResourceDeploymentObservationContext?> LoadAsync(ObservationIdentity key, CancellationToken ct) =>
            contexts.LoadWatchAsync(key, ct);

        public async Task<ObservationOwnership?> ClaimAsync(ResourceDeploymentObservationContext context,
            string instanceId,
            CancellationToken ct) =>
            (await store.ClaimLeaseAsync(context, instanceId, ct)).Value as ObservationOwnership;

        public async Task ReleaseAsync(ObservationOwnership owner, CancellationToken ct) =>
            await store.ReleaseLeaseAsync(owner.Lease, ct);

        public Task RunAsync(ResourceDeploymentObservationContext context,
            ObservationOwnership owner,
            CancellationToken ct)
        {
            var descriptor = provider.GetRequiredService<ResourceDeploymentObserverCatalog>()
                                 .Find(context.Deployment.ResourceType.Value)
                             ?? throw new InvalidOperationException("The observation provider is unavailable.");
            return new ResourceDeploymentObservationOwner(store,
                (IResourceDeploymentObservationChangeSource)provider.GetRequiredService(descriptor.ChangeSourceType!),
                (IResourceDeploymentBoundObserver)provider.GetRequiredService(descriptor.BoundObserverType!),
                provider.GetRequiredService<IObservationReadBudget>(),
                clock,
                metrics).RunAsync(context, owner, ct);
        }
    }
}

public sealed class ResourceDeploymentObservationOwner(
    IResourceDeploymentObservationStore store,
    IResourceDeploymentObservationChangeSource source,
    IResourceDeploymentBoundObserver observer,
    IObservationReadBudget reads,
    TimeProvider clock,
    ResourceDeploymentObservationMetrics? metrics = null)
{
    public Task RunAsync(ResourceDeploymentObservationContext context,
        ObservationOwnership ownership,
        CancellationToken ct) =>
        new ObservationOwner(new Session(store, source, observer, reads, context, ownership, metrics), clock)
            .RunAsync(ownership.SourceControlVersion, ct);

    private sealed class Session(
        IResourceDeploymentObservationStore store,
        IResourceDeploymentObservationChangeSource source,
        IResourceDeploymentBoundObserver observer,
        IObservationReadBudget reads,
        ResourceDeploymentObservationContext context,
        ObservationOwnership ownership,
        ResourceDeploymentObservationMetrics? metrics) : IObservationSession
    {
        public async IAsyncEnumerable<ObservationChange> WatchAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var change in source.WatchAsync(context, ct))
                yield return change.Kind switch
                {
                    ResourceDeploymentObservationChangeKind.SourceReady when change.BindingId is { } id =>
                        new ObservationSourceReady(id),
                    ResourceDeploymentObservationChangeKind.Invalidated => new ObservationInvalidated(),
                    _ => new ObservationSourceLost()
                };
        }

        public async Task<long?> SetSourceAsync(long version, string? sourceId, CancellationToken ct) =>
            (await store.SetSourceStateAsync(ownership.Lease, context, version, sourceId is not null, sourceId, ct))
            .Value is ObservationSourceState accepted
                ? accepted.SourceControlVersion
                : null;

        public async Task<ObservationSampleResult> SampleAsync(ObservationCut cut, CancellationToken ct)
        {
            var observation =
                await reads.ReadAsync(token => observer.ObserveBoundAsync(context, cut.SourceId, token), ct);
            if (observation.BindingId != cut.SourceId)
                return ObservationSampleResult.BindingLost;
            return (await store.TryCommitSnapshotAsync(ownership.Lease,
                    context,
                    cut.SourceVersion,
                    cut.SourceId,
                    observation,
                    ct)).Value switch
            {
                ResourceDeploymentSharedObservation => ObservationSampleResult.Accepted,
                ObservationStoreRejected { Reason: ObservationStoreRejection.BindingChanged } =>
                    ObservationSampleResult.Superseded,
                _ => ObservationSampleResult.OwnerLost
            };
        }

        public async Task<bool> RenewAsync(CancellationToken ct) =>
            (await store.RenewLeaseAsync(ownership.Lease, context, ct)).Value is ObservationMutationAccepted;

        public void RecordSource(bool healthy) => metrics?.RecordSource(healthy);

        public void RecordSample(SampleOutcome outcome, TimeSpan elapsed) => metrics?.RecordSample(outcome switch
        {
            SampleOutcome.Accepted => ObservationSampleOutcome.Accepted,
            SampleOutcome.Rejected => ObservationSampleOutcome.Rejected,
            SampleOutcome.Cancelled => ObservationSampleOutcome.Cancelled,
            _ => ObservationSampleOutcome.Failed
        },
            elapsed);
    }
}
