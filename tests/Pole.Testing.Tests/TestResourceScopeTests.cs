using System.Text.Json;
using Pole.Testing;
using Xunit;

namespace Pole.Testing.Tests;

public sealed class TestResourceScopeTests
{
    [Fact]
    public async Task DisposeAsync_runs_cleanup_in_reverse_order_and_continues_after_failures()
    {
        var calls = new List<string>();
        var resources = new TestResourceScope("cleanup ordering");
        resources.Defer("first", _ =>
        {
            calls.Add("first");
            return Task.CompletedTask;
        });
        resources.Defer("failing", _ =>
        {
            calls.Add("failing");
            throw new InvalidOperationException("expected cleanup failure");
        });
        resources.Defer("last", _ =>
        {
            calls.Add("last");
            return Task.CompletedTask;
        });

        var failure = await Assert.ThrowsAsync<AggregateException>(() => resources.DisposeAsync().AsTask());

        Assert.Equal(["last", "failing", "first"], calls);
        Assert.Contains("failing", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_cleanup_group_starts_all_actions_before_awaiting_completion()
    {
        var resources = new TestResourceScope(
            "concurrent cleanup",
            cleanupTimeout: TimeSpan.FromSeconds(2));
        var group = resources.CreateConcurrentCleanupGroup("namespaces");
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        for (var index = 0; index < 2; index++)
        {
            group.Defer($"namespace {index}", async cancellationToken =>
            {
                if (Interlocked.Increment(ref started) == 2)
                    bothStarted.SetResult();
                await bothStarted.Task.WaitAsync(cancellationToken);
            });
        }

        await resources.DisposeAsync();

        Assert.Equal(2, started);
    }

    [Fact]
    public async Task CreateAsync_registers_only_successfully_created_resources()
    {
        var cleaned = new List<string>();
        var resources = new TestResourceScope("created resources");
        var created = await resources.CreateAsync(
            "created",
            _ => Task.FromResult("created"),
            (resource, _) =>
            {
                cleaned.Add(resource);
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => resources.CreateAsync<string>(
            "not-created",
            _ => throw new InvalidOperationException("create failed"),
            (resource, _) =>
            {
                cleaned.Add(resource);
                return Task.CompletedTask;
            }));
        await resources.DisposeAsync();

        Assert.Equal("created", created);
        Assert.Equal(["created"], cleaned);
    }

    [Fact]
    public async Task CreateAsync_validates_registration_before_creating_the_resource()
    {
        var createCalled = false;
        var resources = new TestResourceScope("registration validation");

        await Assert.ThrowsAsync<ArgumentException>(() => resources.CreateAsync(
            " ",
            _ =>
            {
                createCalled = true;
                return Task.FromResult("resource");
            },
            (_, _) => Task.CompletedTask));
        await resources.DisposeAsync();

        Assert.False(createCalled);
    }

    [Fact]
    public async Task CreateAsync_cleans_a_resource_created_during_concurrent_disposal()
    {
        var createStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCount = 0;
        var resources = new TestResourceScope("create-dispose race");

        var createTask = resources.CreateAsync(
            "racing resource",
            async _ =>
            {
                createStarted.SetResult();
                await finishCreate.Task;
                return "resource";
            },
            (_, _) =>
            {
                cleanupCount++;
                return Task.CompletedTask;
            });
        await createStarted.Task;
        await resources.DisposeAsync();
        finishCreate.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => createTask);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public async Task Cleanup_sequence_attempts_later_steps_after_an_earlier_failure()
    {
        var calls = new List<string>();
        var resources = new TestResourceScope("cleanup sequence");
        var sequence = resources.CreateCleanupSequence("resource deletion");
        sequence.Defer("delete", _ =>
        {
            calls.Add("delete");
            throw new InvalidOperationException("ambiguous delete failure");
        });
        sequence.Defer("observe deletion", _ =>
        {
            calls.Add("observe");
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<AggregateException>(() => resources.DisposeAsync().AsTask());

        Assert.Equal(["delete", "observe"], calls);
    }

    [Fact]
    public async Task Cleanup_group_cleans_a_resource_created_during_concurrent_cleanup()
    {
        var createStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCount = 0;
        var resources = new TestResourceScope("group create-cleanup race");
        var group = resources.CreateConcurrentCleanupGroup("resources");

        var createTask = group.CreateAsync(
            "racing resource",
            async _ =>
            {
                createStarted.SetResult();
                await finishCreate.Task;
                return "resource";
            },
            (_, _) =>
            {
                cleanupCount++;
                return Task.CompletedTask;
            });
        await createStarted.Task;
        await resources.DisposeAsync();
        finishCreate.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => createTask);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        var cleanupCount = 0;
        var resources = new TestResourceScope("idempotent cleanup");
        resources.Defer("resource", _ =>
        {
            cleanupCount++;
            return Task.CompletedTask;
        });

        await resources.DisposeAsync();
        await resources.DisposeAsync();

        Assert.Equal(1, cleanupCount);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public async Task Concurrent_cleanup_group_bounds_running_actions(int? configuredLimit, int expectedConcurrency)
    {
        var resources = new TestResourceScope("bounded cleanup", TimeSpan.FromSeconds(5));
        var group = configuredLimit is { } limit
            ? resources.CreateConcurrentCleanupGroup("independent resources", limit)
            : resources.CreateConcurrentCleanupGroup("independent resources");
        var firstBatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var running = 0;
        var maximumRunning = 0;
        var stateLock = new Lock();
        for (var index = 0; index < 5; index++)
        {
            group.Defer($"resource {index}", async cancellationToken =>
            {
                var ordinal = Interlocked.Increment(ref started);
                lock (stateLock)
                {
                    running++;
                    maximumRunning = Math.Max(maximumRunning, running);
                }
                if (ordinal == expectedConcurrency) firstBatchStarted.SetResult();
                try
                {
                    if (ordinal <= expectedConcurrency) await releaseFirstBatch.Task.WaitAsync(cancellationToken);
                    else await Task.Yield();
                }
                finally
                {
                    lock (stateLock) running--;
                }
            });
        }

        var cleanup = resources.DisposeAsync().AsTask();
        await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedConcurrency, Volatile.Read(ref started));
        releaseFirstBatch.SetResult();
        await cleanup;

        Assert.Equal(5, started);
        Assert.Equal(expectedConcurrency, maximumRunning);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Concurrent_cleanup_group_rejects_non_positive_limits(int maxConcurrency)
    {
        await using var resources = new TestResourceScope("invalid cleanup limit");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            resources.CreateConcurrentCleanupGroup("resources", maxConcurrency));
    }

    [Fact]
    public async Task Concurrent_cleanup_group_attempts_queued_actions_after_budget_cancellation()
    {
        var resources = new TestResourceScope("cleanup cancellation", TimeSpan.FromMilliseconds(100));
        var group = resources.CreateConcurrentCleanupGroup("resources", maxConcurrency: 2);
        var attempted = 0;
        for (var index = 0; index < 4; index++)
        {
            group.Defer($"resource {index}", async cancellationToken =>
            {
                Interlocked.Increment(ref attempted);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        }

        var failure = await Assert.ThrowsAsync<AggregateException>(() => resources.DisposeAsync().AsTask());

        Assert.Equal(4, attempted);
        Assert.Equal(4, Causes(failure).OfType<OperationCanceledException>().Count());
    }

    [Fact]
    public async Task RunAsync_preserves_body_and_all_concurrent_cleanup_failures()
    {
        var resources = new TestResourceScope("body and cleanup failures");
        var group = resources.CreateConcurrentCleanupGroup("resources");
        var bodyFailure = new InvalidOperationException("body failure");
        var firstFailure = new InvalidOperationException("first cleanup failure");
        var secondFailure = new InvalidOperationException("second cleanup failure");
        group.Defer("first", _ => Task.FromException(firstFailure));
        group.Defer("second", _ => Task.FromException(secondFailure));

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            resources.RunAsync(() => Task.FromException(bodyFailure)));
        var failures = Causes(failure).ToArray();

        Assert.Contains(bodyFailure, failures);
        Assert.Contains(firstFailure, failures);
        Assert.Contains(secondFailure, failures);
    }

    [Fact]
    public async Task Cleanup_timings_survive_request_journal_truncation_without_recording_descriptions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pole-cleanup-timing-{Guid.NewGuid():N}");
        try
        {
            var timings = new TestTimings(GetType(), directory: directory);
            var journal = new TestPhaseJournal("bounded request journal", directory);
            using var activation = journal.Activate();
            for (var index = 0; index < 1100; index++) journal.Record(TestPhase.RequestStarted);
            var resources = new TestResourceScope("private-scope-canary", timings: timings);
            resources.Defer("private-resource-canary", _ => Task.CompletedTask, "cleanup.resource.delete");

            await resources.RunAsync(() => Task.CompletedTask);

            using var lastRequest = JsonDocument.Parse(File.ReadLines(journal.Path).Last());
            Assert.Equal("Truncated", lastRequest.RootElement.GetProperty("phase").GetString());
            var timingOutput = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "timing-*.jsonl")));
            Assert.Contains("cleanup.resource.delete", timingOutput);
            Assert.Contains("cleanup.total", timingOutput);
            Assert.DoesNotContain("canary", timingOutput);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<Exception> Causes(Exception exception)
    {
        yield return exception;
        IEnumerable<Exception> children = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } inner ? [inner] : Enumerable.Empty<Exception>();
        foreach (var child in children)
        {
            foreach (var cause in Causes(child))
                yield return cause;
        }
    }
}
