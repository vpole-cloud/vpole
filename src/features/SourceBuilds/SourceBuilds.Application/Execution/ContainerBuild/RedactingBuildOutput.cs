using System.Text;
using System.Text.Json;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Execution.ContainerBuild;

/// <summary>Per-invocation sanitizer. Lookahead prevents split credential emission on either pipe.</summary>
public sealed class RedactingBuildOutput : IAsyncDisposable
{
    private readonly Func<SourceBuildLogStream, string, CancellationToken, Task> sink;
    private readonly string[] patterns;
    private readonly Dictionary<SourceBuildLogStream, string> carry = [];
    private readonly SemaphoreSlim gate = new(1);
    private readonly int lookahead;
    private readonly Action? outputUnavailable;
    private bool disabled;
    private bool unavailable;

    public RedactingBuildOutput(Func<SourceBuildLogStream, string, CancellationToken, Task> sink, string username, string secret, Action? outputUnavailable = null)
    {
        this.sink = sink;
        this.outputUnavailable = outputUnavailable;
        if (username.Length > 8192 || secret.Length > 8192)
        {
            patterns = []; lookahead = 0; disabled = true;
            outputUnavailable?.Invoke();
            return;
        }
        var auth = $"{username}:{secret}";
        patterns = new[] { username, secret, auth, Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)), Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), Convert.ToBase64String(Encoding.UTF8.GetBytes(username)), Uri.EscapeDataString(secret), Uri.EscapeDataString(username), Uri.EscapeDataString(auth), JsonSerializer.Serialize(secret)[1..^1], JsonSerializer.Serialize(username)[1..^1] }
            .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();
        lookahead = patterns.Select(value => value.Length).DefaultIfEmpty(1).Max() - 1;
        // An unusually large known input cannot force unbounded lookahead or escape partial masking.
        disabled = lookahead > 32 * 1024;
        if (disabled) outputUnavailable?.Invoke();
    }

    public async Task WriteAsync(SourceBuildLogStream stream, string text, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (disabled || unavailable) return;
            var input = carry.GetValueOrDefault(stream, "") + text;
            var (output, rest) = Sanitize(input, Math.Max(0, input.Length - lookahead));
            carry[stream] = rest;
            if (output.Length > 0)
            {
                try { await sink(stream, output, ct); }
                catch (Exception) { unavailable = true; outputUnavailable?.Invoke(); throw; }
            }
        }
        finally { gate.Release(); }
    }

    private (string Output, string Remaining) Sanitize(string input, int end)
    {
        var result = new StringBuilder();
        var index = 0;
        while (index < end)
        {
            var match = patterns.FirstOrDefault(pattern => input.AsSpan(index).StartsWith(pattern, StringComparison.Ordinal));
            if (match is not null) { result.Append("[REDACTED]"); index += match.Length; }
            else if (char.IsHighSurrogate(input[index]) && index + 1 < input.Length && char.IsLowSurrogate(input[index + 1]))
            { result.Append(input, index, 2); index += 2; }
            else { result.Append(input[index]); index++; }
        }
        return (result.ToString(), input[index..]);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (unavailable) return;
            if (disabled) await sink(SourceBuildLogStream.System, "Output unavailable: known credential exceeds the sanitizer limit.\n", CancellationToken.None);
            else foreach (var (stream, text) in carry)
            {
                var (output, _) = Sanitize(text, text.Length);
                if (output.Length > 0) await sink(stream, output, CancellationToken.None);
            }
            carry.Clear();
        }
        catch (Exception) { unavailable = true; outputUnavailable?.Invoke(); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
