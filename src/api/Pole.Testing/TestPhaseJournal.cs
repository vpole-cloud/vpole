using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pole.Testing;

public enum TestPhase
{
    Started, Discovery, Selection, Action, RequestStarted, ResponseReceived,
    RequestCancelled, RequestFailed, SnapshotAccepted, StreamOpening, StreamReading,
    StreamEnded, StreamFailed, Cleanup, Completed, Failed, Truncated, BodyCompleted
}

/// <summary>Bounded diagnostic metadata. Caller-owned identifiers are hashed; payloads are never accepted.</summary>
public sealed class TestPhaseJournal
{
    private static readonly AsyncLocal<TestPhaseJournal?> Ambient = new();
    private readonly Lock _lock = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _scenario;
    private int _count;
    private long _nextRequest;
    private bool _unavailable;
    public string Path { get; } = string.Empty;
    public static TestPhaseJournal? Current => Ambient.Value;

    public long AllocateRequestId() => Interlocked.Increment(ref _nextRequest);

    public IDisposable Activate()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Activation(previous);
    }

    public TestPhaseJournal(string scenario, string? directory = null)
    {
        _scenario = Hash(scenario)!;
        try
        {
            directory ??= Environment.GetEnvironmentVariable("POLE_TEST_EVIDENCE_DIRECTORY")
                          ?? System.IO.Path.GetFullPath(System.IO.Path.Combine(".artifacts", "test-phases"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, $"{_scenario}-{Guid.NewGuid():N}.jsonl");
            Record(TestPhase.Started);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                         or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Diagnostics must never prevent the scenario from starting or hide its actual failure.
            _unavailable = true;
        }
    }

    private sealed class Activation(TestPhaseJournal? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }

    public bool Record(
        TestPhase phase,
        string? resourceId = null,
        string? instanceUid = null,
        string? container = null,
        long? request = null,
        int? status = null,
        long? revision = null,
        long? generation = null,
        long? observationVersion = null)
    {
        lock (_lock)
        {
            if (_unavailable || _count >= 1024 || !Enum.IsDefined(phase))
                return false;
            if (_count == 1023)
                phase = TestPhase.Truncated;
            var line = JsonSerializer.Serialize(new
            {
                scenario = _scenario,
                elapsedMs = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                phase = phase.ToString(),
                resource = Hash(resourceId),
                instance = Hash(instanceUid),
                container = Hash(container),
                request,
                status,
                revision,
                generation,
                observationVersion
            });
            try
            {
                File.AppendAllText(Path, line + Environment.NewLine);
                _count++;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Evidence failures must not replace an exception already leaving the scenario.
                _unavailable = true;
                return false;
            }
        }
    }

    private static string? Hash(string? value) =>
        value is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
