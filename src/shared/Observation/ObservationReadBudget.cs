// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Observation;

public class ObservationReadBudget(
    TimeProvider clock
) : IObservationReadBudget, IDisposable
{
    private readonly SemaphoreSlim _capacity = new(ObservationPolicy.ConcurrentReads);

    public void Dispose() => _capacity.Dispose();

    public async Task<T> ReadAsync<T>(
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        var expiresAt = clock.GetUtcNow() + ObservationPolicy.ReadTimeout;
        using var deadline = new CancellationTokenSource(ObservationPolicy.ReadTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        await _capacity.WaitAsync(linked.Token);
        try
        {
            if (clock.GetUtcNow() >= expiresAt)
                await deadline.CancelAsync();
            linked.Token.ThrowIfCancellationRequested();
            var result = await read(linked.Token);
            if (clock.GetUtcNow() >= expiresAt)
                await deadline.CancelAsync();
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _capacity.Release();
        }
    }
}
