using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Realtime;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Observation;

public sealed class StorageObservationCoordinator(
    IServiceScopeFactory scopes,
    StorageObservationOptions options,
    StorageObservationWakeup wakeup,
    TimeProvider clock,
    ILogger<StorageObservationCoordinator> logger) : BackgroundService
{
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, RunningOwner> owners = new(StringComparer.Ordinal);
    private string? cursor;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ScanAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OutOfMemoryException) { logger.LogWarning("Storage observation coordination failed ({FailureType}); durable interests will be retried.", exception.GetType().Name); }
                await wakeup.WaitAsync(clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var owner in owners.Values) await owner.Lifetime.CancelAsync();
            await Task.WhenAll(owners.Values.Select(owner => owner.Task));
            foreach (var owner in owners.Values) owner.Lifetime.Dispose(); owners.Clear();
        }
    }
    private async Task ScanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IStorageObservationStore>(); var contexts = scope.ServiceProvider.GetRequiredService<StorageObservationContextLoader>();
        await store.ReapExpiredDemandsAsync(options.ScanBatchSize, ct);
        foreach (var (target, owner) in owners.ToArray())
        {
            if (owner.Task.IsCompleted) { try { await owner.Task; } finally { owner.Lifetime.Dispose(); owners.Remove(target); } }
            else if ((await store.LoadControlAsync(target, ct))?.Epoch != owner.Epoch || await contexts.LoadAsync(target, ct) != owner.Context) await owner.Lifetime.CancelAsync();
        }
        var page = await store.ScanDemandsAsync(cursor, options.ScanBatchSize, ct); cursor = page.NextCursor;
        foreach (var target in page.DeploymentTargetIds)
        {
            if (owners.ContainsKey(target) || owners.Count >= options.MaximumSources) continue;
            var context = await contexts.LoadAsync(target, ct);
            if (context is null || (await store.ClaimLeaseAsync(context, instanceId, ct)).Value is not StorageObservationOwnership ownership) continue;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct); var task = RunOwnerAsync(context, ownership, lifetime);
            owners.Add(target, new(context, ownership.Lease.Epoch, lifetime, task));
        }
    }
    private async Task RunOwnerAsync(StorageObservationContext context, StorageObservationOwnership ownership, CancellationTokenSource lifetime)
    {
        await using var scope = scopes.CreateAsyncScope(); var store = scope.ServiceProvider.GetRequiredService<IStorageObservationStore>();
        try
        {
            await new StorageObservationOwner(store, scope.ServiceProvider.GetRequiredService<IStorageObservationSource>(), scope.ServiceProvider.GetRequiredService<StorageObservationNativeReader>(), clock).RunAsync(context, ownership, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { logger.LogWarning("Storage observation owner stopped ({FailureType}); coverage returns to polling.", exception.GetType().Name); }
        finally
        {
            await lifetime.CancelAsync(); using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
            try { await store.ReleaseLeaseAsync(ownership.Lease, cleanup.Token); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { logger.LogWarning("Storage observation lease release failed ({FailureType}); server expiry fences the owner.", exception.GetType().Name); }
            wakeup.Notify();
        }
    }
    private sealed record RunningOwner(
        StorageObservationContext Context,
        long Epoch,
        CancellationTokenSource Lifetime,
        Task Task);
}

/// <summary>Negative changes fence persistence immediately; positive cuts are coalesced and single flight.</summary>
internal sealed class StorageObservationOwner(
    IStorageObservationStore store,
    IStorageObservationSource source,
    StorageObservationNativeReader native,
    TimeProvider clock)
{
    private readonly SemaphoreSlim stateGate = new(1);
    private readonly Channel<bool> hints = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private SourceState state = new(0, null, null, null);
    public async Task RunAsync(StorageObservationContext context, StorageObservationOwnership ownership, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct); state = new(ownership.SourceControlVersion, null, null, null);
        var tasks = new[] { PumpAsync(), SampleAsync(), RenewAsync(), ValidateAsync() };
        try { await await Task.WhenAny(tasks); }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { state.SampleLifetime?.Cancel(); state.SampleLifetime?.Dispose(); stateGate.Dispose(); }
        }
        async Task PumpAsync()
        {
            try
            {
                await foreach (var change in source.WatchAsync(context, lifetime.Token))
                {
                    await stateGate.WaitAsync(lifetime.Token);
                    try
                    {
                        if (change.Kind == StorageObservationChangeKind.Changed && change.Inventory is { Complete: true } && state.SourceId == change.SourceId && state.SampleLifetime is not null)
                        { state = state with { Inventory = change.Inventory }; hints.Writer.TryWrite(true); continue; }
                        var healthy = change.Kind == StorageObservationChangeKind.Ready && change.SourceId is not null && change.Inventory is { Complete: true };
                        var outcome = await store.SetSourceStateAsync(ownership.Lease, context, state.Version, healthy, healthy ? change.SourceId : null, lifetime.Token);
                        if (outcome.Value is not StorageObservationSourceState accepted) { await lifetime.CancelAsync(); return; }
                        // Control was fenced BEFORE canceling or awaiting recovery/native sampling.
                        state.SampleLifetime?.Cancel(); state.SampleLifetime?.Dispose();
                        state = new(accepted.SourceControlVersion, healthy ? change.SourceId : null, healthy ? change.Inventory : null, healthy ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token) : null);
                        if (healthy) hints.Writer.TryWrite(true);
                    }
                    finally { stateGate.Release(); }
                }
            }
            finally { await lifetime.CancelAsync(); }
        }
        async Task SampleAsync()
        {
            while (await hints.Reader.WaitToReadAsync(lifetime.Token))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, lifetime.Token); while (hints.Reader.TryRead(out _)) { }
                SourceState captured; CancellationToken sampleToken;
                await stateGate.WaitAsync(lifetime.Token);
                try { captured = state; sampleToken = captured.SampleLifetime?.Token ?? lifetime.Token; }
                finally { stateGate.Release(); }
                if (captured.SourceId is null || captured.Inventory is null || captured.SampleLifetime is null) continue;
                try
                {
                    // Watch frames are hints. A delayed frame must never replace newer live evidence.
                    var inventory = await native.ReadAsync(context, sampleToken);
                    if (inventory is null)
                    {
                        await stateGate.WaitAsync(lifetime.Token);
                        try
                        {
                            if (state.Version != captured.Version) continue;
                            await store.SetSourceStateAsync(ownership.Lease, context, state.Version, false, null, lifetime.Token);
                            await lifetime.CancelAsync();
                        }
                        finally { stateGate.Release(); }
                        return;
                    }
                    var outcome = await store.TryCommitSnapshotAsync(ownership.Lease, context, captured.Version, captured.SourceId, inventory, sampleToken);
                    if (outcome.Value is StorageObservationRejected { Reason: not StorageObservationRejection.SourceChanged }) await lifetime.CancelAsync();
                }
                catch (OperationCanceledException) when (sampleToken.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
            }
        }
        async Task RenewAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), clock, lifetime.Token);
                if ((await store.RenewLeaseAsync(ownership.Lease, context, lifetime.Token)).Value is StorageObservationRejected) { await lifetime.CancelAsync(); return; }
            }
        }
        async Task ValidateAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), clock, lifetime.Token);
                hints.Writer.TryWrite(true);
            }
        }
    }
    private sealed record SourceState(
        long Version,
        string? SourceId,
        StorageInventoryObservation? Inventory,
        CancellationTokenSource? SampleLifetime);
}
