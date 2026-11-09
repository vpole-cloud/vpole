using System.Runtime.CompilerServices;
using System.Text.Json;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class LifecycleEvidenceTests
{
    private const string Tenant = "507f1f77bcf86cd799439011";
    private const string Resource = "507f1f77bcf86cd799439012";
    private const string Source = "507f1f77bcf86cd799439013";
    private const string Run = "507f1f77bcf86cd799439014";
    private const string Instant = "2026-11-09T12:00:00.1234567Z";
    private const string RuntimeIdentity = $"TenantId={Tenant}, RuntimeCellId={Resource}, Generation=2, CheckSequence=1";
    private static readonly DateTimeOffset RecordedAt = new(2026, 11, 9, 12, 0, 1, TimeSpan.Zero);
    public static TheoryData<string, string, string> OwnedMessages => new()
    {
        { $"RuntimeCell deletion check scheduled: {RuntimeIdentity}, NotBeforeUtc={Instant}", "runtime-cell", "scheduled" },
        { $"RuntimeCell deletion work started: {RuntimeIdentity}, NotBeforeUtc={Instant}, StartedAtUtc={Instant}", "runtime-cell", "started" },
        { $"RuntimeCell deletion work started: {RuntimeIdentity}, NotBeforeUtc=(null), StartedAtUtc={Instant}", "runtime-cell", "started" },
        { $"RuntimeCell native absence confirmed: {RuntimeIdentity}, ObservedAtUtc={Instant}", "runtime-cell", "native-absent" },
        { $"RuntimeCell deletion finalized and target reference released: {RuntimeIdentity}, CommittedAtUtc={Instant}", "runtime-cell", "finalized" },
        { $"GitOps work scheduled for source {Source}; notBeforeUtc={Instant}, scheduledAtUtc={Instant}", "gitops", "scheduled" },
        { $"GitOps attempt started for source {Source}, run {Run}, attempt 1; startedAtUtc={Instant}", "gitops", "started" },
        { $"GitOps lifecycle action completed for run {Run}, operation WebService/{Resource}, action Remove; completedAtUtc={Instant}", "gitops", "action-completed" }
    };

    [Theory]
    [MemberData(nameof(OwnedMessages))]
    public void ExactOwnedMessages_BecomeScalarEvidenceWithHashedIdentities(string message, string component, string phase)
    {
        Assert.True(LifecycleEvidence.TryParse("      " + message, RecordedAt, out var record));
        Assert.NotNull(record);
        Assert.Equal(component, record.Component);
        Assert.Equal(phase, record.Phase);
        Assert.Equal(RecordedAt, record.RecordedAtUtc);
        var serialized = JsonSerializer.Serialize(record);
        foreach (var id in new[] { Tenant, Resource, Source, Run }) Assert.DoesNotContain(id, serialized);
        Assert.DoesNotContain(message, serialized);
        if (record.OccurredAtUtc is { } occurred)
            Assert.Equal(1234567, occurred.Ticks % TimeSpan.TicksPerSecond);
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJ-secret-payload")]
    [InlineData("GitOps work scheduled for source provider-password; notBeforeUtc=2026-11-09T12:00:00.1234567Z, scheduledAtUtc=2026-11-09T12:00:00.1234567Z")]
    [InlineData("GitOps lifecycle action completed for run 507f1f77bcf86cd799439014, operation Secret/private-password, action Remove; completedAtUtc=2026-11-09T12:00:00.1234567Z")]
    public void UnknownOrCredentialBearingLogs_AreRejected(string line) =>
        Assert.False(LifecycleEvidence.TryParse(line, RecordedAt, out _));

    [Theory]
    [InlineData("trailing")]
    [InlineData("prefix")]
    [InlineData("newline")]
    [InlineData("invalid-date")]
    [InlineData("overflow")]
    [InlineData("zero-generation")]
    [InlineData("negative-sequence")]
    [InlineData("ansi")]
    public void NearMatchesAndMalformedValues_AreRejected(string mutation)
    {
        var line = $"RuntimeCell native absence confirmed: {RuntimeIdentity}, ObservedAtUtc={Instant}";
        line = mutation switch
        {
            "trailing" => line + " password=private",
            "prefix" => "Error payload: " + line,
            "newline" => line + "\n",
            "invalid-date" => line.Replace("2026-11-09", "2026-99-99"),
            "overflow" => line.Replace("Generation=2", "Generation=9999999999999999999"),
            "zero-generation" => line.Replace("Generation=2", "Generation=0"),
            "negative-sequence" => line.Replace("CheckSequence=1", "CheckSequence=-1"),
            "ansi" => "\u001b[32m" + line,
            _ => throw new InvalidOperationException()
        };
        Assert.False(LifecycleEvidence.TryParse(line, RecordedAt, out _));
    }

    [Fact]
    public async Task Collector_WritesOnlyParsedEventsAndAwaitsLiveReaderCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pole-lifecycle-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var collector = new LifecycleEvidence(directory);
            collector.Start(Snapshot(), Live());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await collector.DisposeAsync();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await collector.DisposeAsync();
            Assert.False(collector.StorageFailed);
            Assert.False(collector.SourceFailed);
            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "lifecycle-events.jsonl"));
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line =>
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal("runtime-cell", document.RootElement.GetProperty("component").GetString());
                Assert.DoesNotContain("private-password", line);
                Assert.DoesNotContain(Tenant, line);
            });
        }
        finally { Directory.Delete(directory, true); }

        async IAsyncEnumerable<string> Snapshot()
        {
            yield return "credentials=private-password";
            yield return $"RuntimeCell native absence confirmed: {RuntimeIdentity}, ObservedAtUtc={Instant}";
            await Task.CompletedTask;
        }
        async IAsyncEnumerable<string> Live([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return $"RuntimeCell deletion finalized and target reference released: {RuntimeIdentity}, CommittedAtUtc={Instant}";
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally { stopped.TrySetResult(); }
        }
    }

    [Fact]
    public async Task DiagnosticStorageFailure_DoesNotReplaceTheScenarioFailure()
    {
        var file = Path.GetTempFileName();
        try
        {
            var failure = new InvalidOperationException("Controlled scenario failure.");
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var collector = new LifecycleEvidence(file);
                Assert.True(collector.StorageFailed);
                throw failure;
            });
            Assert.Same(failure, actual);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task DiagnosticSourceFailure_IsObservedWithoutFaultingDisposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pole-lifecycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var collector = new LifecycleEvidence(directory);
            collector.Start(Failed(), Failed());
            await collector.DisposeAsync();
            Assert.True(collector.SourceFailed);
        }
        finally { Directory.Delete(directory, true); }
        async IAsyncEnumerable<string> Failed()
        {
            yield return "ignored unrelated diagnostic";
            await Task.CompletedTask;
            throw new InvalidOperationException("private-source-log-content");
        }
    }
}
