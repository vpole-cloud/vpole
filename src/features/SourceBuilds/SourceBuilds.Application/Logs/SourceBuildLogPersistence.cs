using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Logs;

public sealed record SourceBuildLogIdentity(
    string TenantId,
    string WebServiceId,
    string JobId,
    string WriterToken
);

public sealed record SourceBuildLogWriteAccepted(
    SourceBuildLog Log
);

public sealed record SourceBuildLogWriteFenced;

public union SourceBuildLogWriteOutcome(SourceBuildLogWriteAccepted, SourceBuildLogWriteFenced);

internal sealed class SourceBuildLogPersistence(
    IMongoCollection<SourceBuildLog> logs,
    TimeProvider timeProvider
)
{
    public async Task<SourceBuildLogWriteOutcome> StartAsync(SourceBuildLog log, CancellationToken ct = default)
    {
        try
        {
            await logs.InsertOneAsync(log, cancellationToken: ct);
            return new SourceBuildLogWriteAccepted(log);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var current = await LoadAsync(log.TenantId, log.WebServiceId, log.Id, ct);
            return current?.WriterToken == log.WriterToken
                ? new SourceBuildLogWriteAccepted(current)
                : new SourceBuildLogWriteFenced();
        }
    }

    public Task<SourceBuildLog?> LoadAsync(
        string tenantId,
        string webServiceId,
        string jobId,
        CancellationToken ct = default) =>
        logs.Find(SourceBuildLogFilters.Owned(tenantId, webServiceId, jobId)).FirstOrDefaultAsync(ct)!;

    public Task<SourceBuildLogWriteOutcome> AppendAsync(
        SourceBuildLogIdentity identity,
        long sequence,
        SourceBuildLogStream stream,
        string text,
        CancellationToken ct = default) => MutateAsync(
        identity,
        log => log.Append(sequence, stream, text, timeProvider.GetUtcNow().UtcDateTime),
        ct);

    public Task<SourceBuildLogWriteOutcome>
        RenewAsync(SourceBuildLogIdentity identity, CancellationToken ct = default) => MutateAsync(
        identity,
        log => log.Renew(timeProvider.GetUtcNow().UtcDateTime),
        ct);

    public Task<SourceBuildLogWriteOutcome> SealAsync(
        SourceBuildLogIdentity identity,
        bool unavailable,
        CancellationToken ct = default) => MutateAsync(
        identity,
        log => log.Seal(timeProvider.GetUtcNow().UtcDateTime, unavailable),
        ct);

    private async Task<SourceBuildLogWriteOutcome> MutateAsync(
        SourceBuildLogIdentity identity,
        Func<SourceBuildLog, SourceBuildLog> mutation,
        CancellationToken ct)
    {
        // Heartbeats, appends and cleanup contend on one document. Retry only a changed CAS;
        // an acknowledged duplicate append returns the same range, never appends twice.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var current = await logs.Find(SourceBuildLogFilters.Writer(identity)).FirstOrDefaultAsync(ct);
            if (current is null || current.State is not OpenSourceBuildLog open || now >= open.LeaseExpiresAtUtc ||
                now >= current.ExpiresAtUtc)
                return new SourceBuildLogWriteFenced();
            SourceBuildLog replacement;
            try
            {
                replacement = mutation(current);
            }
            catch (InvalidOperationException)
            {
                return new SourceBuildLogWriteFenced();
            }

            if (ReferenceEquals(current, replacement))
                return new SourceBuildLogWriteAccepted(current);
            var result = await logs.ReplaceOneAsync(
                SourceBuildLogFilters.WritableAtRevision(current, now),
                replacement,
                cancellationToken: ct);
            if (result.ModifiedCount == 1)
                return new SourceBuildLogWriteAccepted(replacement);
        }

        return new SourceBuildLogWriteFenced();
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = await logs.Find(SourceBuildLogFilters.Recoverable(now)).Limit(100).ToListAsync(ct);
        foreach (var current in expired)
        {
            var recovered = current.Recover(now);
            await logs.ReplaceOneAsync(SourceBuildLogFilters.AtRevision(current), recovered, cancellationToken: ct);
        }
    }
}
