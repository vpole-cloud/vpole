using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Pole.Features.ResourceDeployments.Application.Diagnostics;

public sealed record RuntimeLogFrame(
    string Kind,
    string? Text,
    string? Reason,
    int OutputBytes);

/// <summary>A finite, cancellable recent/follow read. Runtime output is never persisted.</summary>
public sealed class RuntimeLogStreamResult(PreparedRuntimeLogSession session, ResourceDeploymentDiagnostics diagnostics) : IResult
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext context)
    {
        await using var owner = session;
        var request = context.RequestAborted;
        var lifetime = session.Lifetime;
        var reason = "EndOfWindow";
        ExceptionDispatchInfo? unexpected = null;
        Task monitor = Task.CompletedTask;
        var bytes = 0;
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            using var initialCheck = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            initialCheck.CancelAfter(TimeSpan.FromSeconds(15));
            var changed = !await diagnostics.IsCurrentAsync(session.Deployment, initialCheck.Token) ? "Superseded" : await session.Source.VerifyAsync(initialCheck.Token);
            if (changed is not null) reason = changed;
            else
            {
                await WriteAsync(new("Opened", null, null, 0), lifetime.Token);
                monitor = MonitorAsync();
                using var reader = new StreamReader(session.Source.Output, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                var buffer = new char[2048];
                var carry = "";
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), lifetime.Token);
                    var text = carry + new string(buffer, 0, count);
                    carry = "";
                    if (count > 0 && text.Length > 0 && char.IsHighSurrogate(text[^1])) { carry = text[^1..]; text = text[..^1]; }
                    if (text.Length > 0)
                    {
                        var prefix = Utf8Prefix(text, diagnostics.Options.MaximumBytes - bytes);
                        bytes += Encoding.UTF8.GetByteCount(prefix);
                        if (prefix.Length > 0) await WriteAsync(new("Output", prefix, null, bytes), lifetime.Token);
                        if (prefix.Length < text.Length || bytes >= diagnostics.Options.MaximumBytes) { reason = "OutputLimit"; break; }
                    }
                    if (count == 0) break;
                }
                // An upstream EOF can be the pod disappearing or an API transport closing.
                // Recheck ownership/placement before calling it a completed log window.
                if (reason == "EndOfWindow")
                {
                    using var terminalCheck = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    terminalCheck.CancelAfter(TimeSpan.FromSeconds(5));
                    var terminalReason = !await diagnostics.IsCurrentAsync(session.Deployment, terminalCheck.Token) ? "Superseded" :
                        await session.Source.VerifyAsync(terminalCheck.Token) ?? (session.Follow ? "StreamEnded" : "EndOfWindow");
                    terminalCheck.Token.ThrowIfCancellationRequested();
                    if (reason == "EndOfWindow") reason = terminalReason;
                }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested || diagnostics.Shutdown.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { if (reason == "EndOfWindow") reason = lifetime.IsCancellationRequested ? "LifetimeLimit" : "Timeout"; }
        catch (Exception exception) when (RuntimeDiagnosticFailures.Reason(exception) is { }) { reason = RuntimeDiagnosticFailures.Reason(exception)!; }
        finally
        {
            await lifetime.CancelAsync();
            await monitor;
        }
        unexpected?.Throw();
        if (request.IsCancellationRequested || diagnostics.Shutdown.IsCancellationRequested) return;
        using var finish = CancellationTokenSource.CreateLinkedTokenSource(request, diagnostics.Shutdown);
        finish.CancelAfter(TimeSpan.FromSeconds(5));
        await WriteAsync(new("Ended", null, reason, bytes), finish.Token);

        async Task WriteAsync(RuntimeLogFrame frame, CancellationToken ct)
        {
            await JsonSerializer.SerializeAsync(context.Response.Body, frame, Json, ct);
            await context.Response.WriteAsync("\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
        async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(diagnostics.Options.OwnershipCheckSeconds), lifetime.Token);
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    check.CancelAfter(TimeSpan.FromSeconds(5));
                    string? changed;
                    try { changed = !await diagnostics.IsCurrentAsync(session.Deployment, check.Token) ? "Superseded" : await session.Source.VerifyAsync(check.Token); }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { changed = "Timeout"; }
                    if (changed is not null) { reason = changed; await lifetime.CancelAsync(); return; }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception) when (RuntimeDiagnosticFailures.Reason(exception) is { }) { reason = RuntimeDiagnosticFailures.Reason(exception)!; await lifetime.CancelAsync(); }
            catch (Exception exception) { unexpected = ExceptionDispatchInfo.Capture(exception); await lifetime.CancelAsync(); }
        }
    }

    private static string Utf8Prefix(string text, int maximum)
    {
        var characters = 0; var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximum) break;
            bytes += rune.Utf8SequenceLength; characters += rune.Utf16SequenceLength;
        }
        return text[..characters];
    }
}
