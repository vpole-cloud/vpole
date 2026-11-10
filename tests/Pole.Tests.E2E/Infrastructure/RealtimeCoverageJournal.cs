using System.Text.Json;

namespace Pole.Tests.E2E.Infrastructure;

// Current subscription evidence, never a union of historical acknowledgements.
internal sealed class RealtimeCoverageJournal
{
    private readonly Dictionary<int, Connection> connections = new();

    private readonly object sync = new();
    private long document;
    private int latestConnection;

    public void Navigate()
    {
        lock (sync)
        {
            document++;
            connections.Clear();
        }
    }

    public void Open(int connection, long openedAt)
    {
        lock (sync)
        {
            connections[connection] = new Connection(document, openedAt);
            latestConnection = connection;
        }
    }

    public void Close(int connection)
    {
        lock (sync)
        {
            if (!connections.TryGetValue(connection, out var state))
                return;
            state.Closed = true;
            Reset(state);
        }
    }

    public bool IsCurrent(int connection)
    {
        lock (sync)
        {
            return Current(connection) is not null;
        }
    }

    public void Sent(int connection, JsonElement message)
    {
        lock (sync)
        {
            var state = Current(connection);
            if (state is null || !message.TryGetProperty("type", out var type) || type.GetInt32() != 1 ||
                !message.TryGetProperty("target", out var target) ||
                !message.TryGetProperty("invocationId", out var id))
                return;
            var method = target.GetString();
            if (method is not ("Subscribe" or "Heartbeat" or "Unsubscribe"))
                return;
            if (method is "Subscribe" or "Unsubscribe")
                Reset(state);
            state.Invocations[id.GetString()!] = new Invocation(method, state.Epoch, ++state.Sequence);
        }
    }

    public void Received(
        int connection,
        JsonElement message,
        long receivedAt)
    {
        lock (sync)
        {
            var state = Current(connection);
            if (state is null)
                return;
            if (message.TryGetProperty("target", out var target) &&
                target.GetString() is "SessionRevoked" or "AccessRevoked")
            {
                Reset(state);
                return;
            }

            if (!message.TryGetProperty("type", out var type) || type.GetInt32() != 3 ||
                !message.TryGetProperty("invocationId", out var id) ||
                !state.Invocations.Remove(id.GetString()!, out var invocation) ||
                invocation.Method is not ("Subscribe" or "Heartbeat") || invocation.Epoch != state.Epoch ||
                invocation.Sequence < state.AcceptedSequence)
                return;
            // Clear before decoding: malformed/failed acknowledgements cannot preserve true coverage.
            state.Coverage = [];
            state.AcceptedSequence = invocation.Sequence;
            state.ReceivedAt = receivedAt;
            if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return;
            state.Coverage = result.EnumerateArray()
                .Select(item => new Coverage(
                    item.GetProperty("topic").GetString()!,
                    OptionalText(item, "tenantId"),
                    OptionalText(item, "resourceId"),
                    item.GetProperty("covered").GetBoolean()))
                .ToArray();
        }
    }

    public int LatestCoveredConnection(
        Func<IReadOnlyList<Coverage>, bool> matches,
        long after = 0,
        bool requireOpenedAfter = false)
    {
        lock (sync)
        {
            var state = Current(latestConnection);
            return state is not null && state.ReceivedAt > after && (!requireOpenedAfter || state.OpenedAt > after) &&
                matches(state.Coverage)
                    ? latestConnection
                    : 0;
        }
    }

    private Connection? Current(int connection) => connection == latestConnection &&
        connections.TryGetValue(connection, out var state) && !state.Closed && state.Document == document
            ? state
            : null;

    private static void Reset(Connection state)
    {
        state.Epoch++;
        state.Coverage = [];
        state.Invocations.Clear();
    }

    private static string? OptionalText(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal sealed record Coverage(
        string Topic,
        string? TenantId,
        string? ResourceId,
        bool Covered
    );

    private sealed record Invocation(
        string Method,
        long Epoch,
        long Sequence
    );

    private sealed class Connection(
        long document,
        long openedAt
    )
    {
        public long Document { get; } = document;
        public long OpenedAt { get; } = openedAt;
        public bool Closed { get; set; }
        public long Epoch { get; set; }
        public long Sequence { get; set; }
        public long AcceptedSequence { get; set; }
        public long ReceivedAt { get; set; }
        public Coverage[] Coverage { get; set; } = [];
        public Dictionary<string, Invocation> Invocations { get; } = new();
    }
}
