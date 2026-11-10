using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.SourceBuilds.Domain;

public sealed record SourceBuildLogLimits(
    int MaxBytes,
    int MaxEntries,
    int MaxEntryBytes,
    int RetentionDays
)
{
    public void Validate()
    {
        if (MaxBytes is < 4096 or > 4 * 1024 * 1024 || MaxEntries is < 16 or > 4096 ||
            MaxEntryBytes is < 256 or > 8192 || MaxEntryBytes > MaxBytes || RetentionDays is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(SourceBuildLogLimits));
    }
}

public enum SourceBuildLogStream { System, StandardOutput, StandardError }

public sealed record SourceBuildLogEntry(
    long Sequence,
    SourceBuildLogStream Stream,
    string Text,
    DateTime RecordedAtUtc
)
{
    public override string ToString() => $"SourceBuildLogEntry(sequence={Sequence}, output=[REDACTED])";
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(OpenSourceBuildLog),
    typeof(CompletedSourceBuildLog),
    typeof(InterruptedSourceBuildLog),
    typeof(ExpiredSourceBuildLog),
    typeof(UnavailableSourceBuildLog))]
public closed record SourceBuildLogState;

public sealed record OpenSourceBuildLog(
    DateTime LeaseExpiresAtUtc
) : SourceBuildLogState;

public sealed record CompletedSourceBuildLog(
    DateTime CompletedAtUtc
) : SourceBuildLogState;

public sealed record InterruptedSourceBuildLog(
    DateTime InterruptedAtUtc
) : SourceBuildLogState;

public sealed record ExpiredSourceBuildLog(
    DateTime ExpiredAtUtc
) : SourceBuildLogState;

public sealed record UnavailableSourceBuildLog(
    DateTime UnavailableAtUtc
) : SourceBuildLogState;

/// <summary>A bounded, sanitized capture, independent of the build/deployment job state.</summary>
public sealed record SourceBuildLog(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string WebServiceId,
    string WriterToken,
    SourceBuildLogLimits Limits,
    SourceBuildLogState State,
    ImmutableList<SourceBuildLogEntry> Entries,
    long LastSequence,
    string? LastAppendFingerprint,
    int CapturedBytes,
    bool Truncated,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc
) : ITenantOwned
{
    public SourceBuildLog Append(
        long sequence,
        SourceBuildLogStream stream,
        string text,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(stream) || Encoding.UTF8.GetByteCount(text) > Limits.MaxEntryBytes)
            throw new ArgumentOutOfRangeException(nameof(text));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{stream}:{text}")));
        if (sequence == LastSequence && fingerprint == LastAppendFingerprint)
            return this;
        EnsureOpen(now);
        if (sequence != LastSequence + 1)
            throw new InvalidOperationException("The log append sequence changed.");
        if (Truncated || text.Length == 0)
            return this;
        var prefix = Utf8Prefix(text, Limits.MaxBytes - CapturedBytes);
        var bytes = Encoding.UTF8.GetByteCount(prefix);
        var entries = prefix.Length == 0
            ? Entries
            : Entries.Add(new SourceBuildLogEntry(sequence, stream, prefix, now));
        return this with
        {
            Entries = entries,
            LastSequence = prefix.Length == 0 ? LastSequence : sequence,
            LastAppendFingerprint = fingerprint,
            CapturedBytes = CapturedBytes + bytes,
            Truncated = prefix.Length != text.Length || CapturedBytes + bytes >= Limits.MaxBytes ||
            entries.Count >= Limits.MaxEntries,
            Revision = checked(Revision + 1),
        };
    }

    public SourceBuildLog Renew(DateTime now)
    {
        EnsureOpen(now);
        return this with { State = new OpenSourceBuildLog(now.AddMinutes(2)), Revision = checked(Revision + 1) };
    }

    public SourceBuildLog Seal(DateTime now, bool unavailable = false)
    {
        EnsureOpen(now);
        return this with
        {
            State = unavailable ? new UnavailableSourceBuildLog(now) : new CompletedSourceBuildLog(now),
            Revision = checked(Revision + 1),
        };
    }

    public SourceBuildLog Recover(DateTime now) => now >= ExpiresAtUtc && State is not ExpiredSourceBuildLog
        ?
        this with
        {
            Entries = [], LastAppendFingerprint = null, State = new ExpiredSourceBuildLog(now),
            Revision = checked(Revision + 1),
        }
        : State is OpenSourceBuildLog open && now >= open.LeaseExpiresAtUtc
            ? this with { State = new InterruptedSourceBuildLog(now), Revision = checked(Revision + 1) }
            : this;

    private void EnsureOpen(DateTime now)
    {
        if (State is not OpenSourceBuildLog open || now >= open.LeaseExpiresAtUtc || now >= ExpiresAtUtc)
            throw new InvalidOperationException("The log capture is no longer writable.");
    }

    public static string Utf8Prefix(string text, int maximumBytes)
    {
        var characters = 0;
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes)
                break;
            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }

        return text[..characters];
    }

    public override string ToString() => $"SourceBuildLog(job={Id}, sequence={LastSequence}, output=[REDACTED])";
}

public static class SourceBuildLogFactory
{
    public static SourceBuildLog Create(
        string jobId,
        string tenantId,
        string webServiceId,
        string writerToken,
        SourceBuildLogLimits limits,
        DateTime now)
    {
        limits.Validate();
        return new SourceBuildLog(
            jobId,
            tenantId,
            webServiceId,
            writerToken,
            limits,
            new OpenSourceBuildLog(now.AddMinutes(2)),
            [],
            0,
            null,
            0,
            false,
            1,
            now,
            now.AddDays(limits.RetentionDays));
    }
}
