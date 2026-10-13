using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Observation;

public sealed class ResourceDeploymentObservationReadBudget(TimeProvider clock)
    : IResourceDeploymentObservationReadBudget, IDisposable
{
    private readonly SemaphoreSlim _capacity = new(4);

    public async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        var expiresAt = clock.GetUtcNow() + TimeSpan.FromSeconds(15);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        await _capacity.WaitAsync(linked.Token);
        try
        {
            if (clock.GetUtcNow() >= expiresAt) await deadline.CancelAsync();
            linked.Token.ThrowIfCancellationRequested();
            var result = await read(linked.Token);
            if (clock.GetUtcNow() >= expiresAt) await deadline.CancelAsync();
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally { _capacity.Release(); }
    }

    public void Dispose() => _capacity.Dispose();
}
