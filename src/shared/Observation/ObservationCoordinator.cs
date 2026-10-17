using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pole.Observation;

public sealed record ObservationPage<TKey>(
    IReadOnlyList<TKey> Items,
    string? NextCursor);

/// <summary>A scoped feature adapter; identity, eligibility and transactional ownership remain feature-owned.</summary>
public interface IObservationCoordination<TKey, TContext, TOwner>
    where TKey : notnull where TContext : class where TOwner : class
{
    Task ReapAsync(int limit, CancellationToken ct);
    Task<ObservationPage<TKey>> ScanAsync(string? cursor, int limit, CancellationToken ct);
    Task<bool> IsCurrentAsync(TKey key, TContext context, TOwner owner, CancellationToken ct);
    Task<TContext?> LoadAsync(TKey key, CancellationToken ct);
    Task<TOwner?> ClaimAsync(TContext context, string instanceId, CancellationToken ct);
    Task RunAsync(TContext context, TOwner owner, CancellationToken ct);
    Task ReleaseAsync(TOwner owner, CancellationToken ct);
}

/// <summary>One bounded demand/owner scheduler for both tenant workloads and platform discovery.</summary>
public abstract class ObservationCoordinator<TKey, TContext, TOwner>(
    IServiceScopeFactory scopes,
    bool enabled,
    int maximumSources,
    int scanBatchSize,
    ObservationWakeup wakeup,
    TimeProvider clock,
    ILogger logger) : BackgroundService
    where TKey : notnull where TContext : class where TOwner : class
{
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<TKey, RunningOwner> owners = [];
    private string? cursor;
    protected abstract IObservationCoordination<TKey, TContext, TOwner> CreateAdapter(IServiceProvider provider);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled)
            return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ScanAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    logger.LogWarning("Observation coordination failed ({FailureType}); durable interests will retry.",
                        exception.GetType().Name);
                }

                await wakeup.WaitAsync(clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var owner in owners.Values)
                await owner.Lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(owners.Values.Select(owner => owner.Task));
            }
            finally
            {
                foreach (var owner in owners.Values)
                    owner.Lifetime.Dispose();
                owners.Clear();
            }
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var adapter = CreateAdapter(scope.ServiceProvider);
        await adapter.ReapAsync(scanBatchSize, ct);
        foreach (var (key, owner) in owners.ToArray())
        {
            if (owner.Task.IsCompleted)
            {
                try
                {
                    await owner.Task;
                }
                finally
                {
                    owner.Lifetime.Dispose();
                    owners.Remove(key);
                }
            }
            else if (!await adapter.IsCurrentAsync(key, owner.Context, owner.Ownership, ct))
                await owner.Lifetime.CancelAsync();
        }

        var page = await adapter.ScanAsync(cursor, scanBatchSize, ct);
        cursor = page.NextCursor;
        foreach (var key in page.Items)
        {
            if (owners.ContainsKey(key) || owners.Count >= maximumSources)
                continue;
            var context = await adapter.LoadAsync(key, ct);
            if (context is null || await adapter.ClaimAsync(context, instanceId, ct) is not { } ownership)
                continue;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            owners.Add(key, new(context, ownership, lifetime, RunOwnerAsync(context, ownership, lifetime)));
        }
    }

    private async Task RunOwnerAsync(TContext context, TOwner owner, CancellationTokenSource lifetime)
    {
        await using var scope = scopes.CreateAsyncScope();
        var adapter = CreateAdapter(scope.ServiceProvider);
        try
        {
            await adapter.RunAsync(context, owner, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning("Observation owner stopped ({FailureType}); coverage returns to polling.",
                exception.GetType().Name);
        }
        finally
        {
            await lifetime.CancelAsync();
            using var cleanup = new CancellationTokenSource(ObservationPolicy.CleanupTimeout, clock);
            try
            {
                await adapter.ReleaseAsync(owner, cleanup.Token);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogWarning("Observation release failed ({FailureType}); server expiry fences the owner.",
                    exception.GetType().Name);
            }

            wakeup.Notify();
        }
    }

    private sealed record RunningOwner(
        TContext Context,
        TOwner Ownership,
        CancellationTokenSource Lifetime,
        Task Task);
}
