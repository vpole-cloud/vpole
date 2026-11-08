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
}
