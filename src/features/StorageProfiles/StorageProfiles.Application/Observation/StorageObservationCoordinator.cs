using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Realtime;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Observation;

namespace Pole.Features.StorageProfiles.Application.Observation;

public sealed class StorageObservationCoordinator(
    IServiceScopeFactory scopes,
    StorageObservationOptions options,
    StorageObservationWakeup wakeup,
    TimeProvider clock,
    ILogger<StorageObservationCoordinator> logger,
    StorageObservationMetrics? metrics = null)
    : ObservationCoordinator<string, StorageObservationContext, StorageObservationOwnership>(
        scopes,
        options.Enabled,
        options.MaximumSources,
        options.ScanBatchSize,
        wakeup,
        clock,
        logger)
{
    protected override IObservationCoordination<string, StorageObservationContext, StorageObservationOwnership>
        CreateAdapter(IServiceProvider provider) =>
        new Adapter(provider, clock, metrics);

    private sealed class Adapter(IServiceProvider provider, TimeProvider clock, StorageObservationMetrics? metrics)
        : IObservationCoordination<string, StorageObservationContext, StorageObservationOwnership>
    {
        private readonly IStorageObservationStore store = provider.GetRequiredService<IStorageObservationStore>();

        private readonly StorageObservationContextLoader contexts =
            provider.GetRequiredService<StorageObservationContextLoader>();

        public Task ReapAsync(int limit, CancellationToken ct) => store.ReapExpiredDemandsAsync(limit, ct);

        public async Task<ObservationPage<string>> ScanAsync(string? cursor, int limit, CancellationToken ct)
        {
            var page = await store.ScanDemandsAsync(cursor, limit, ct);
            return new(page.DeploymentTargetIds, page.NextCursor);
        }

        public async Task<bool> IsCurrentAsync(string key,
            StorageObservationContext context,
            StorageObservationOwnership owner,
            CancellationToken ct) =>
            (await store.LoadControlAsync(key, ct))?.Epoch == owner.Lease.Epoch &&
            await contexts.LoadAsync(key, ct) == context;

        public Task<StorageObservationContext?> LoadAsync(string key, CancellationToken ct) =>
            contexts.LoadAsync(key, ct);

        public async Task<StorageObservationOwnership?> ClaimAsync(StorageObservationContext context,
            string instanceId,
            CancellationToken ct) =>
            (await store.ClaimLeaseAsync(context, instanceId, ct)).Value as StorageObservationOwnership;

        public async Task ReleaseAsync(StorageObservationOwnership owner, CancellationToken ct) =>
            await store.ReleaseLeaseAsync(owner.Lease, ct);

        public Task RunAsync(StorageObservationContext context,
            StorageObservationOwnership owner,
            CancellationToken ct) =>
            new StorageObservationOwner(store,
                provider.GetRequiredService<IStorageObservationSource>(),
                provider.GetRequiredService<StorageObservationNativeReader>(),
                clock,
                metrics).RunAsync(context, owner, ct);
    }
}

internal sealed class StorageObservationOwner(
    IStorageObservationStore store,
    IStorageObservationSource source,
    StorageObservationNativeReader native,
    TimeProvider clock,
    StorageObservationMetrics? metrics = null)
{
    public Task RunAsync(StorageObservationContext context,
        StorageObservationOwnership ownership,
        CancellationToken ct) =>
        new ObservationOwner(new Session(store, source, native, context, ownership, metrics), clock)
            .RunAsync(ownership.SourceControlVersion, ct);

    private sealed class Session(
        IStorageObservationStore store,
        IStorageObservationSource source,
        StorageObservationNativeReader native,
        StorageObservationContext context,
        StorageObservationOwnership ownership,
        StorageObservationMetrics? metrics) : IObservationSession
    {
        public async IAsyncEnumerable<ObservationChange> WatchAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var change in source.WatchAsync(context, ct))
                yield return change switch
                {
                    { Kind: StorageObservationChangeKind.Ready, SourceId: { } id, Inventory.Complete: true } =>
                        new ObservationSourceReady(id),
                    { Kind: StorageObservationChangeKind.Changed, SourceId: { } id, Inventory.Complete: true } =>
                        new ObservationInvalidated(id),
                    _ => new ObservationSourceLost()
                };
        }

        public async Task<long?> SetSourceAsync(long version, string? sourceId, CancellationToken ct) =>
            (await store.SetSourceStateAsync(ownership.Lease, context, version, sourceId is not null, sourceId, ct))
            .Value is StorageObservationSourceState accepted
                ? accepted.SourceControlVersion
                : null;

        public async Task<ObservationSampleResult> SampleAsync(ObservationCut cut, CancellationToken ct)
        {
            // Watch cuts remain hints; only current native evidence can be committed.
            var inventory = await native.ReadAsync(context, ct);
            if (inventory is null)
                return ObservationSampleResult.Unavailable;
            return (await store.TryCommitSnapshotAsync(ownership.Lease,
                    context,
                    cut.SourceVersion,
                    cut.SourceId,
                    inventory,
                    ct)).Value switch
            {
                StorageObservationSnapshot => ObservationSampleResult.Accepted,
                StorageObservationRejected
                {
                    Reason: StorageObservationRejection.SourceChanged
                } => ObservationSampleResult.Superseded,
                _ => ObservationSampleResult.OwnerLost
            };
        }

        public async Task<bool> RenewAsync(CancellationToken ct) =>
            (await store.RenewLeaseAsync(ownership.Lease, context, ct)).Value is StorageObservationMutationAccepted;

        public void RecordSource(bool healthy) => metrics?.RecordSource(healthy);

        public void RecordSample(ObservationSampleMetric outcome, TimeSpan elapsed) => metrics?.RecordSample(
            outcome switch
            {
                ObservationSampleMetric.Accepted => StorageObservationSampleOutcome.Accepted,
                ObservationSampleMetric.Rejected => StorageObservationSampleOutcome.Rejected,
                ObservationSampleMetric.Cancelled => StorageObservationSampleOutcome.Cancelled,
                _ => StorageObservationSampleOutcome.Failed
            },
            elapsed);
    }
}
