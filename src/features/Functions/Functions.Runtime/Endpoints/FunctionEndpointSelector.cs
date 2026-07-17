using System.Collections.Concurrent;

namespace Pole.Features.Functions.Runtime.Endpoints;

public interface IFunctionEndpointSelector
{
    IReadOnlyList<FunctionEndpoint> Order(FunctionEndpointSnapshot snapshot);
}

public sealed class FunctionEndpointSelector(TimeProvider timeProvider) : IFunctionEndpointSelector
{
    private static readonly TimeSpan CursorRetention = TimeSpan.FromHours(1);
    private sealed record CursorState(int Cursor, long LastUsed);

    private readonly ConcurrentDictionary<FunctionServiceKey, CursorState> cursors = [];
    private int selections;

    public IReadOnlyList<FunctionEndpoint> Order(FunctionEndpointSnapshot snapshot)
    {
        var selectable = snapshot.Endpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.TargetUid))
            .ToArray();
        if (selectable.Length == 0)
            return [];
        var now = timeProvider.GetTimestamp();
        if ((Interlocked.Increment(ref selections) & 1023) == 0)
        {
            foreach (var stale in cursors
                         .Where(entry => timeProvider.GetElapsedTime(entry.Value.LastUsed, now) > CursorRetention)
                         .Select(entry => entry.Key))
                cursors.TryRemove(stale, out _);
        }
        var state = cursors.AddOrUpdate(
            snapshot.Service,
            _ => new CursorState(0, now),
            (_, current) => new CursorState(unchecked(current.Cursor + 1), now));
        var start = (int)((uint)state.Cursor % (uint)selectable.Length);
        return Enumerable.Range(0, selectable.Length)
            .Select(offset => selectable[(start + offset) % selectable.Length])
            .ToArray();
    }
}
