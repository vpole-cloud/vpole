using Pole.Features.SourceBuilds.Application.Execution.ContainerBuild;
using System.Text;
using System.Text.Json;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Domain;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class ProcessOutputTests
{
    [Fact]
    public async Task Known_credentials_and_auth_forms_are_masked_at_every_split_boundary()
    {
        const string username = "robot$platform";
        const string secret = "platform-secret-Ω";
        var forms = new[] { username, secret, $"{username}:{secret}", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{secret}")), Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), Convert.ToBase64String(Encoding.UTF8.GetBytes(username)), Uri.EscapeDataString(secret), Uri.EscapeDataString(username), Uri.EscapeDataString($"{username}:{secret}"), JsonSerializer.Serialize(secret)[1..^1], JsonSerializer.Serialize(username)[1..^1] };
        foreach (var form in forms)
            for (var split = 0; split <= form.Length; split++)
            {
                var output = new StringBuilder();
                await using (var capture = new RedactingBuildOutput((_, text, _) => { output.Append(text); return Task.CompletedTask; }, username, secret))
                {
                    await capture.WriteAsync(SourceBuildLogStream.StandardOutput, $"before🙂 {form[..split]}");
                    await capture.WriteAsync(SourceBuildLogStream.StandardOutput, $"{form[split..]} after\n");
                }
                Assert.Equal("before🙂 [REDACTED] after\n", output.ToString());
            }
    }

    [Fact]
    public async Task Concurrent_pipes_have_independent_redaction_carry_and_flush_safe_tail()
    {
        var output = new Dictionary<SourceBuildLogStream, StringBuilder>();
        await using (var capture = new RedactingBuildOutput((stream, text, _) => { if (!output.ContainsKey(stream)) output[stream] = new(); output[stream].Append(text); return Task.CompletedTask; }, "robot-user", "credential-canary"))
        {
            await Task.WhenAll(capture.WriteAsync(SourceBuildLogStream.StandardOutput, "out creden"), capture.WriteAsync(SourceBuildLogStream.StandardError, "err creden"));
            await Task.WhenAll(capture.WriteAsync(SourceBuildLogStream.StandardOutput, "tial-canary\n"), capture.WriteAsync(SourceBuildLogStream.StandardError, "tial-canary\n"));
        }
        Assert.Equal("out [REDACTED]\n", output[SourceBuildLogStream.StandardOutput].ToString());
        Assert.Equal("err [REDACTED]\n", output[SourceBuildLogStream.StandardError].ToString());
    }

    [Fact]
    public async Task Process_output_is_delivered_before_exit_and_cancellation_kills_and_drains_it()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new ProcessRunner().InvokeAsync(new("python3", ["-u", "-c", "import time; print('live-prefix', flush=True); time.sleep(30)"], Path.GetTempPath(), new Dictionary<string, string>(),
            Output: (_, text, _) => { if (text.Contains("live-prefix", StringComparison.Ordinal)) observed.TrySetResult(); return Task.CompletedTask; }), lifetime.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
        }
        finally { await lifetime.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Large_output_is_drained_after_sink_failure_and_classification_prefix_is_bounded()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new ProcessRunner().InvokeAsync(new("python3", ["-u", "-c", "import sys; sys.stdout.write('x'*200000); sys.stdout.flush(); sys.stderr.write('y'*200000); sys.stderr.flush()"], Path.GetTempPath(), new Dictionary<string, string>(), 64,
            Output: (_, _, _) => throw new IOException("simulated capture outage")), lifetime.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(64, result.StandardOutput.Length);
        Assert.Equal(64, result.StandardError.Length);
        Assert.True(result.OutputCaptureFailed);
    }

    [Fact]
    public async Task Oversized_known_input_disables_raw_output_with_an_explicit_unavailable_signal()
    {
        var unavailable = false;
        var output = new StringBuilder();
        var knownInput = new string('s', 10000);
        await using (var capture = new RedactingBuildOutput((_, text, _) => { output.Append(text); return Task.CompletedTask; }, "robot", knownInput, () => unavailable = true))
            await capture.WriteAsync(SourceBuildLogStream.StandardError, knownInput);
        Assert.True(unavailable);
        Assert.DoesNotContain(knownInput, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Output unavailable", output.ToString(), StringComparison.Ordinal);
    }
}
