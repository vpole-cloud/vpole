using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Observation;

/// <summary>Local wakeups are coalesced; durable demand remains authoritative across replicas.</summary>
public sealed class ResourceDeploymentObservationWakeup
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    public void Notify() => channel.Writer.TryWrite(true);
    internal async Task WaitAsync(TimeProvider clock, CancellationToken ct)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = channel.Reader.ReadAsync(turn.Token).AsTask();
        var timer = Task.Delay(TimeSpan.FromSeconds(1), clock, turn.Token);
        await Task.WhenAny(read, timer);
        await turn.CancelAsync();
        try { await Task.WhenAll(read, timer); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

public sealed class ResourceDeploymentObservationCoordinator(
    IServiceScopeFactory scopes,
    ResourceDeploymentObservationOptions options,
    ResourceDeploymentObservationWakeup wakeup,
    TimeProvider clock,
    ILogger<ResourceDeploymentObservationCoordinator> logger,
    ResourceDeploymentObservationMetrics? metrics = null) : BackgroundService
{
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<ObservationIdentity, RunningOwner> owners = [];
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
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { logger.LogWarning("Runtime observation coordination failed ({FailureType}); durable demand will be retried.", exception.GetType().Name); }
                await wakeup.WaitAsync(clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var owner in owners.Values) await owner.Lifetime.CancelAsync();
            await Task.WhenAll(owners.Values.Select(owner => owner.Task));
            foreach (var owner in owners.Values) owner.Lifetime.Dispose();
            owners.Clear();
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IResourceDeploymentObservationStore>();
        var contexts = scope.ServiceProvider.GetRequiredService<ResourceDeploymentObservationContextLoader>();
        await store.ReapExpiredDemandsAsync(options.ScanBatchSize, ct);
        foreach (var (identity, owner) in owners.ToArray())
        {
            if (owner.Task.IsCompleted)
            {
                try { await owner.Task; }
                finally { owner.Lifetime.Dispose(); owners.Remove(identity); }
            }
            else if ((await store.LoadControlAsync(identity, ct))?.Epoch != owner.OwnerEpoch ||
                !SameContext(await contexts.LoadWatchAsync(identity, ct), owner.Context))
                await owner.Lifetime.CancelAsync();
        }
        var page = await store.ScanDemandsAsync(cursor, options.ScanBatchSize, ct);
        cursor = page.NextCursor;
        foreach (var identity in page.Items)
        {
            if (owners.ContainsKey(identity) || owners.Count >= options.MaximumSources) continue;
            var context = await contexts.LoadWatchAsync(identity, ct);
            if (context is null || (await store.ClaimLeaseAsync(context, instanceId, ct)).Value is not ObservationOwnership ownership) continue;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = RunOwnerAsync(context, ownership, lifetime);
            owners.Add(identity, new(context, ownership.Lease.Epoch, lifetime, task));
        }
    }

    private async Task RunOwnerAsync(ResourceDeploymentObservationContext context, ObservationOwnership ownership, CancellationTokenSource lifetime)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IResourceDeploymentObservationStore>();
        try
        {
            var descriptor = scope.ServiceProvider.GetRequiredService<ResourceDeploymentObserverCatalog>().Find(context.Deployment.ResourceType.Value)
                ?? throw new InvalidOperationException("The observation provider is unavailable.");
            var source = (IResourceDeploymentObservationChangeSource)scope.ServiceProvider.GetRequiredService(descriptor.ChangeSourceType!);
            var observer = (IResourceDeploymentBoundObserver)scope.ServiceProvider.GetRequiredService(descriptor.BoundObserverType!);
            var reads = scope.ServiceProvider.GetRequiredService<IResourceDeploymentObservationReadBudget>();
            await new ResourceDeploymentObservationOwner(store, source, observer, reads, clock, metrics)
                .RunAsync(context, ownership, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { logger.LogWarning("Runtime observation owner stopped ({FailureType}); coverage returns to polling.", exception.GetType().Name); }
        finally
        {
            await lifetime.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
            try { await store.ReleaseLeaseAsync(ownership.Lease, cleanup.Token); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { logger.LogWarning("Runtime observation lease release failed ({FailureType}); server-time expiry fences the owner.", exception.GetType().Name); }
            wakeup.Notify();
        }
    }

    private static bool SameContext(ResourceDeploymentObservationContext? current, ResourceDeploymentObservationContext expected) =>
        current is not null && current.DeploymentRevision == expected.DeploymentRevision && current.TargetConnectionVersion == expected.TargetConnectionVersion &&
        current.Deployment.Id == expected.Deployment.Id && current.Deployment.TenantId == expected.Deployment.TenantId &&
        current.Deployment.ResourceType == expected.Deployment.ResourceType && current.Deployment.ResourceId == expected.Deployment.ResourceId &&
        current.Deployment.ResourceGroupId == expected.Deployment.ResourceGroupId && current.Deployment.IsolationScopeId == expected.Deployment.IsolationScopeId &&
        current.Deployment.RuntimeCellId == expected.Deployment.RuntimeCellId && current.Deployment.ResourceRevision == expected.Deployment.ResourceRevision &&
        current.Deployment.DesiredGeneration == expected.Deployment.DesiredGeneration && current.Deployment.Placement.DeploymentTargetId == expected.Deployment.Placement.DeploymentTargetId &&
        current.Deployment.Placement.TargetWorkloadConfigurationRevision == expected.Deployment.Placement.TargetWorkloadConfigurationRevision &&
        current.Deployment.Placement.KubernetesNamespace == expected.Deployment.Placement.KubernetesNamespace;

    private sealed record RunningOwner(
        ResourceDeploymentObservationContext Context,
        long OwnerEpoch,
        CancellationTokenSource Lifetime,
        Task Task);
}

/// <summary>Negative source transitions bypass debounce and fence an in-flight sample in Mongo.</summary>
public sealed class ResourceDeploymentObservationOwner(
    IResourceDeploymentObservationStore store,
    IResourceDeploymentObservationChangeSource source,
    IResourceDeploymentBoundObserver observer,
    IResourceDeploymentObservationReadBudget reads,
    TimeProvider clock,
    ResourceDeploymentObservationMetrics? metrics = null)
{
    private readonly SemaphoreSlim stateGate = new(1);
    private readonly Channel<bool> hints = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private SourceState state = new(0, null, null);

    public async Task RunAsync(ResourceDeploymentObservationContext context, ObservationOwnership ownership, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        state = new(ownership.SourceControlVersion, null, null);
        var tasks = new[] { PumpAsync(), SampleAsync(), RenewAsync(), ValidateAsync() };
        try { await await Task.WhenAny(tasks); }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                try { await Task.WhenAll(tasks); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
            finally
            {
                await stateGate.WaitAsync(CancellationToken.None);
                try { state.SampleLifetime?.Cancel(); state.SampleLifetime?.Dispose(); }
                finally { stateGate.Release(); stateGate.Dispose(); }
            }
        }

        async Task PumpAsync()
        {
            try
            {
                await foreach (var change in source.WatchAsync(context, lifetime.Token))
                {
                    if (change.Kind == ResourceDeploymentObservationChangeKind.Invalidated) { hints.Writer.TryWrite(true); continue; }
                    await SetSourceAsync(change.Kind == ResourceDeploymentObservationChangeKind.SourceReady, change.BindingId);
                }
            }
            finally { await lifetime.CancelAsync(); }
        }

        async Task SetSourceAsync(bool healthy, string? binding)
        {
            if (!healthy) binding = null;
            await stateGate.WaitAsync(lifetime.Token);
            try
            {
                var outcome = await store.SetSourceStateAsync(ownership.Lease, context, state.Version, healthy, binding, lifetime.Token);
                if (outcome.Value is not ObservationSourceState accepted) { await lifetime.CancelAsync(); return; }
                if (accepted.SourceControlVersion == state.Version && state.Binding == binding) return;
                if ((state.Binding is not null) != healthy) metrics?.RecordSource(healthy);
                // Persistence changed its fence before cancellation or any later sample.
                state.SampleLifetime?.Cancel();
                state.SampleLifetime?.Dispose();
                state = new(accepted.SourceControlVersion, binding,
                    healthy ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token) : null);
                if (healthy) hints.Writer.TryWrite(true);
            }
            finally { stateGate.Release(); }
        }

        async Task SampleAsync()
        {
            while (await hints.Reader.WaitToReadAsync(lifetime.Token))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, lifetime.Token);
                while (hints.Reader.TryRead(out _)) { }
                SourceState captured;
                CancellationToken sampleToken;
                await stateGate.WaitAsync(lifetime.Token);
                try { captured = state; sampleToken = captured.SampleLifetime?.Token ?? lifetime.Token; }
                finally { stateGate.Release(); }
                if (captured.Binding is null || captured.SampleLifetime is null) continue;
                var started = Stopwatch.GetTimestamp();
                var sampleOutcome = ObservationSampleOutcome.Failed;
                try
                {
                    var observation = await reads.ReadAsync(token => observer.ObserveBoundAsync(context, captured.Binding, token), sampleToken);
                    if (observation.BindingId != captured.Binding) { sampleOutcome = ObservationSampleOutcome.Rejected; await SetSourceAsync(false, null); continue; }
                    var result = await store.TryCommitSnapshotAsync(ownership.Lease, context, captured.Version, captured.Binding, observation, sampleToken);
                    sampleOutcome = result.Value is ResourceDeploymentSharedObservation ? ObservationSampleOutcome.Accepted : ObservationSampleOutcome.Rejected;
                    if (result.Value is ObservationStoreRejected { Reason: not ObservationStoreRejection.BindingChanged })
                        await lifetime.CancelAsync();
                }
                catch (OperationCanceledException) when (sampleToken.IsCancellationRequested && !lifetime.IsCancellationRequested) { sampleOutcome = ObservationSampleOutcome.Cancelled; }
                catch (OperationCanceledException) { sampleOutcome = ObservationSampleOutcome.Cancelled; throw; }
                finally { metrics?.RecordSample(sampleOutcome, Stopwatch.GetElapsedTime(started)); }
            }
        }

        async Task RenewAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), clock, lifetime.Token);
                if ((await store.RenewLeaseAsync(ownership.Lease, context, lifetime.Token)).Value is ObservationStoreRejected)
                { await lifetime.CancelAsync(); return; }
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
        string? Binding,
        CancellationTokenSource? SampleLifetime);
}
