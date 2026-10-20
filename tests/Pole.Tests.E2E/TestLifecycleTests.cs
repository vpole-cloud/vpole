using System.Diagnostics;
using System.Net;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class TestLifecycleTests
{
    [Fact]
    public async Task Body_and_cleanup_failures_are_preserved_and_later_cleanup_still_runs()
    {
        var scope = new TestResourceScope("controlled failure");
        var original = new InvalidOperationException("body sentinel");
        var cleanup = new IOException("cleanup sentinel");
        var calls = new List<string>();
        scope.Defer("dispose", _ => { calls.Add("disposed"); return Task.CompletedTask; });
        scope.Defer("stop", _ => { calls.Add("stopped"); throw cleanup; });
        var failure = await Assert.ThrowsAsync<AggregateException>(() => scope.RunAsync(() => throw original));
        Assert.Equal(["stopped", "disposed"], calls);
        Assert.Contains(original, failure.Flatten().InnerExceptions);
        Assert.Contains(failure.Flatten().InnerExceptions, value => ReferenceEquals(value.InnerException, cleanup));
    }

    [Fact]
    public async Task Cleanup_runs_after_a_partially_completed_mutation_fails()
    {
        var scope = new TestResourceScope("unknown mutation outcome");
        var state = true;
        scope.Defer("restore", _ => { state = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => scope.RunAsync(() =>
        {
            state = false;
            throw new IOException("lost mutation response");
        }));
        Assert.True(state);
    }

    [Fact]
    public async Task Local_deadline_bounds_a_stalled_probe_and_cancels_its_token()
    {
        var stalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        await Assert.ThrowsAsync<TimeoutException>(() => TestWait.UntilAsync(token =>
        {
            observed = token;
            return stalled.Task;
        }, TimeSpan.FromMilliseconds(50), "controlled stalled probe").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(observed.IsCancellationRequested);
        stalled.SetResult(true);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public void Unexpected_deletion_status_is_an_immediate_safe_failure(int status)
    {
        var failure = Assert.Throws<HttpRequestException>(() => TestDeletion.IsComplete((HttpStatusCode)status));
        Assert.Equal((HttpStatusCode)status, failure.StatusCode);
        Assert.Equal($"Deletion verification returned HTTP {status}.", failure.Message);
    }

    [Fact]
    public void Only_not_found_completes_deletion()
    {
        Assert.False(TestDeletion.IsComplete(HttpStatusCode.OK));
        Assert.True(TestDeletion.IsComplete(HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task Startup_cancellation_does_not_stop_running_port_forward_pipe_readers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pole-pipe-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var startup = new CancellationTokenSource();
        KubernetesPortForwardSession? session = null;
        try
        {
            var start = new ProcessStartInfo("python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("""
                import pathlib, sys, time
                root = pathlib.Path(sys.argv[1])
                print('Forwarding from 127.0.0.1:12345 -> 80', flush=True)
                while not (root / 'write').exists(): time.sleep(0.01)
                for _ in range(1024):
                    print('x' * 1024, flush=True)
                    print('y' * 1024, file=sys.stderr, flush=True)
                (root / 'drained').touch()
                while True: time.sleep(1)
                """);
            start.ArgumentList.Add(directory);
            session = await KubernetesPortForwardSession.StartProcessAsync(start, 12345, startup.Token);
            await startup.CancelAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "write"), string.Empty);
            await TestWait.UntilAsync(_ => Task.FromResult(File.Exists(Path.Combine(directory, "drained"))),
                TimeSpan.FromSeconds(10), "Both pipes must keep draining after the startup budget expires.");
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
