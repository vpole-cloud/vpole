using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;

namespace Pole.Testing;

/// <summary>Opt-in phase timings. Accepts code-owned names, never request values or Mongo payloads.</summary>
public sealed class TestTimings
{
    private static readonly AsyncLocal<Measurement?> Current = new();
    private static readonly ConcurrentDictionary<(ConnectionId, int), PendingCommand> Commands = new();
    private static readonly Lock FileLock = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly string _kind;
    private readonly string _owner;
    private string? _path;

    public TestTimings(
        Type owner,
        bool fixture = false,
        string? directory = null)
    {
        _owner = owner.FullName ?? owner.Name;
        _kind = fixture ? "fixture" : "test";
        directory ??= Environment.GetEnvironmentVariable("POLE_TEST_TIMING_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, $"timing-{Environment.ProcessId}.jsonl");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Diagnostic storage must not prevent tests from running.
        }
    }

    public static bool Enabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POLE_TEST_TIMING_DIRECTORY"));

    public IDisposable Measure(string phase) =>
        _path is null ? EmptyMeasurement.Instance : new Measurement(this, phase);

    public IAsyncDisposable MeasureDisposal(IAsyncDisposable resource, string phase) =>
        new TimedDisposal(this, resource, phase);

    public static void CommandStarted(CommandStartedEvent command)
    {
        if (Current.Value is { IsActive: true } measurement)
            Commands[(command.ConnectionId, command.RequestId)] = new PendingCommand(measurement, command.CommandName);
    }

    public static void CommandSucceeded(CommandSucceededEvent command) => FinishCommand(
        command.ConnectionId,
        command.RequestId,
        command.Duration,
        false);

    public static void CommandFailed(CommandFailedEvent command) => FinishCommand(
        command.ConnectionId,
        command.RequestId,
        command.Duration,
        true);

    private static void FinishCommand(
        ConnectionId connection,
        int request,
        TimeSpan duration,
        bool failed)
    {
        if (Commands.TryRemove((connection, request), out var pending))
            pending.Measurement.RecordCommand(pending.Name, duration, failed);
    }

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or SecurityException;

    private sealed class TimedDisposal(
        TestTimings timings,
        IAsyncDisposable resource,
        string phase
    ) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            using (timings.Measure(phase))
            {
                await resource.DisposeAsync();
            }
        }
    }

    private sealed record PendingCommand(
        Measurement Measurement,
        string Name
    );

    private sealed class CommandTotals
    {
        public long Count;
        public long Failed;
        public long Ticks;
    }

    private sealed class Measurement : IDisposable
    {
        private readonly ConcurrentDictionary<string, CommandTotals> _mongo = new();
        private readonly TestTimings _owner;
        private readonly string _phase;
        private readonly Measurement? _previous;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
        private int _disposed;

        public Measurement(TestTimings owner, string phase)
        {
            _owner = owner;
            _phase = phase;
            _previous = Current.Value;
            Current.Value = this;
        }

        public bool IsActive => Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            var finishedUtc = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            Current.Value = _previous;
            var line = JsonSerializer.Serialize(
                new
                {
                    owner = _owner._owner,
                    instance = _owner._instance,
                    kind = _owner._kind,
                    phase = _phase,
                    startedUtc = _startedUtc,
                    finishedUtc,
                    elapsedMs = elapsed,
                    mongo = _mongo.OrderBy(pair => pair.Key)
                        .Select(pair => new
                        {
                            command = pair.Key,
                            count = pair.Value.Count,
                            failed = pair.Value.Failed,
                            elapsedMs = TimeSpan.FromTicks(pair.Value.Ticks).TotalMilliseconds,
                        })
                        .ToArray(),
                });
            lock (FileLock)
            {
                if (_owner._path is null)
                    return;
                try
                {
                    File.AppendAllText(_owner._path, line + Environment.NewLine);
                }
                catch (Exception exception) when (IsStorageFailure(exception))
                {
                    _owner._path = null;
                }
            }
        }

        public void RecordCommand(
            string name,
            TimeSpan duration,
            bool failed)
        {
            var totals = _mongo.GetOrAdd(name, _ => new CommandTotals());
            Interlocked.Increment(ref totals.Count);
            if (failed)
                Interlocked.Increment(ref totals.Failed);
            Interlocked.Add(ref totals.Ticks, duration.Ticks);
        }
    }

    private sealed class EmptyMeasurement : IDisposable
    {
        public static readonly EmptyMeasurement Instance = new();

        public void Dispose()
        {
        }
    }
}
