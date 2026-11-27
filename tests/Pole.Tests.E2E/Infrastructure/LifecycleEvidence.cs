// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace Pole.Tests.E2E.Infrastructure;

internal sealed record LifecycleEvidenceRecord(
    string Component,
    string Phase,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset? NotBeforeUtc,
    string? TenantHash,
    string? ResourceHash,
    string? SourceHash,
    string? RunHash,
    string? OperationHash,
    long? Generation,
    long? CheckSequence,
    long? Attempt,
    string? Action
);

/// <summary>Only exact application-owned lifecycle messages may become evidence; raw log text is never stored.</summary>
internal sealed class LifecycleEvidence : IAsyncDisposable
{
    private const string Id = "[0-9a-f]{24}";
    private const string Number = "[0-9]{1,19}";
    private const string Instant = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z";

    private const string RuntimeIdentity
        = $"TenantId=(?<tenant>{Id}), RuntimeCellId=(?<resource>{Id}), Generation=(?<generation>{Number}), CheckSequence=(?<sequence>{Number})";

    private static readonly (string Component, string Phase, Regex Pattern)[] Patterns =
    [
        Pattern(
            "runtime-cell",
            "scheduled",
            $"RuntimeCell deletion check scheduled: {RuntimeIdentity}, NotBeforeUtc=(?<due>{Instant})"),
        Pattern(
            "runtime-cell",
            "started",
            $"RuntimeCell deletion work started: {RuntimeIdentity}, NotBeforeUtc=(?<due>{Instant}|\\(null\\)), StartedAtUtc=(?<occurred>{Instant})"),
        Pattern(
            "runtime-cell",
            "native-absent",
            $"RuntimeCell native absence confirmed: {RuntimeIdentity}, ObservedAtUtc=(?<occurred>{Instant})"),
        Pattern(
            "runtime-cell",
            "finalized",
            $"RuntimeCell deletion finalized and target reference released: {RuntimeIdentity}, CommittedAtUtc=(?<occurred>{Instant})"),
        Pattern(
            "gitops",
            "scheduled",
            $"GitOps work scheduled for source (?<source>{Id}); notBeforeUtc=(?<due>{Instant}), scheduledAtUtc=(?<occurred>{Instant})"),
        Pattern(
            "gitops",
            "started",
            $"GitOps attempt started for source (?<source>{Id}), run (?<run>{Id}), attempt (?<attempt>{Number}); startedAtUtc=(?<occurred>{Instant})"),
        Pattern(
            "gitops",
            "action-completed",
            $"GitOps lifecycle action completed for run (?<run>{Id}), operation (?<operation>[A-Za-z][A-Za-z0-9]{{0,63}}/{Id}), action (?<action>Adopt|Remove|Replace|Detach|Apply|Retain); completedAtUtc=(?<occurred>{Instant})"),
    ];

    private static readonly Regex AspireEnvelope = new(
        @"\A(?<timestamp>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6,7}Z) (?<message>.*)\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking);

    private static readonly JsonSerializerOptions JsonOptions = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly CancellationTokenSource _shutdown = new();
    private readonly StreamWriter? _writer;
    private int _disposed;
    private Task? _reader;

    public LifecycleEvidence(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;
        try
        {
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(
                new FileStream(
                    Path.Combine(directory, "lifecycle-events.jsonl"),
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    useAsync: true)) { AutoFlush = true };
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            StorageFailed = true;
        }
    }

    public bool StorageFailed { get; private set; }
    public bool SourceFailed { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _shutdown.CancelAsync();
        }
        catch (Exception)
        {
            SourceFailed = true;
        }

        if (_reader is not null)
            await _reader;
        if (_writer is not null)
            try
            {
                await _writer.DisposeAsync();
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                StorageFailed = true;
            }

        _shutdown.Dispose();
    }

    public static async IAsyncEnumerable<string> ResourceLines(
        ResourceLoggerService logger,
        IResource resource,
        bool live,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Typed overloads resolve the current model's DCP instance suffix; literal display names do not.
        var batches = live ? logger.WatchAsync(resource) : logger.GetAllAsync(resource);
        await foreach (var batch in batches.WithCancellation(cancellationToken))
        foreach (var line in batch)
            yield return line.Content;
    }

    public void Start(IAsyncEnumerable<string> snapshot, IAsyncEnumerable<string> live)
    {
        if (_writer is not null && _reader is null)
            _reader = ReadAsync(snapshot, live);
    }

    private async Task ReadAsync(IAsyncEnumerable<string> snapshot, IAsyncEnumerable<string> live)
    {
        await TryCopyAsync(snapshot);
        // Snapshot access can be unavailable before Aspire starts. Keep the live subscription active.
        if (!_shutdown.IsCancellationRequested && !StorageFailed)
            await TryCopyAsync(live);
    }

    private async Task TryCopyAsync(IAsyncEnumerable<string> source)
    {
        try
        {
            await CopyAsync(source);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            StorageFailed = true;
        }
        catch (Exception)
        {
            SourceFailed = true;
        }
    }

    private async Task CopyAsync(IAsyncEnumerable<string> source)
    {
        await foreach (var line in source.WithCancellation(_shutdown.Token))
            if (TryParse(line, DateTimeOffset.UtcNow, out var record))
                await _writer!.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions));
    }

    public static bool TryParse(
        string line,
        DateTimeOffset recordedAtUtc,
        out LifecycleEvidenceRecord? record)
    {
        record = null;
        // Accept only console indentation and Aspire's validated UTC envelope before an owned message.
        if (line.Length > 1024 || line.Contains('\r') || line.Contains('\n'))
            return false;
        line = line.TrimStart(' ');
        // Aspire's LogLine.Content prepends its own UTC console timestamp to resource output.
        var envelope = AspireEnvelope.Match(line);
        if (envelope.Success)
        {
            if (!DateTimeOffset.TryParseExact(
                    envelope.Groups["timestamp"].Value,
                    ["yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _))
                return false;
            line = envelope.Groups["message"].Value.TrimStart(' ');
        }

        if (!line.StartsWith("RuntimeCell ", StringComparison.Ordinal) &&
            !line.StartsWith("GitOps ", StringComparison.Ordinal))
            return false;
        foreach (var (component, phase, pattern) in Patterns)
        {
            var match = pattern.Match(line);
            if (!match.Success)
                continue;
            DateTimeOffset? occurred = null, due = null;
            foreach (var name in new[] { "occurred", "due" })
            {
                var value = match.Groups[name].Value;
                if (value.Length == 0 || value == "(null)")
                    continue;
                if (!DateTimeOffset.TryParseExact(
                        value,
                        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var instant))
                    return false;
                if (name == "occurred")
                    occurred = instant;
                else
                    due = instant;
            }

            if (!TryNumber(match, "generation", positive: true, out var generation) ||
                !TryNumber(match, "sequence", positive: false, out var sequence) || !TryNumber(
                    match,
                    "attempt",
                    positive: true,
                    out var attempt))
                return false;
            record = new LifecycleEvidenceRecord(
                component,
                phase,
                recordedAtUtc.ToUniversalTime(),
                occurred,
                due,
                IdentityHash(match, "tenant"),
                IdentityHash(match, "resource"),
                IdentityHash(match, "source"),
                IdentityHash(match, "run"),
                IdentityHash(match, "operation"),
                generation,
                sequence,
                attempt,
                match.Groups["action"].Success ? match.Groups["action"].Value : null);
            return true;
        }

        return false;
    }

    private static (string, string, Regex) Pattern(
        string component,
        string phase,
        string body) => (component, phase,
        new Regex(
            @"\A" + body + @"\z",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking));

    private static bool TryNumber(
        Match match,
        string name,
        bool positive,
        out long? number)
    {
        number = null;
        if (!match.Groups[name].Success)
            return true;
        if (!long.TryParse(match.Groups[name].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < (positive ? 1 : 0))
            return false;
        number = parsed;
        return true;
    }

    private static string? IdentityHash(Match match, string name) => match.Groups[name].Success
        ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(match.Groups[name].Value)))
        : null;

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException or NotSupportedException or ArgumentException;
}
