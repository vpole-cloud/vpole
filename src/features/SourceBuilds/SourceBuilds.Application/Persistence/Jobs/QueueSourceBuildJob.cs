using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public delegate Task<SourceBuildJob> QueueSourceBuildJob(
    SourceBuildJob job,
    SourceBuildRequestedEvent requestedEvent,
    CancellationToken cancellationToken = default);

internal sealed class MongoQueueSourceBuildJob(
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<SourceBuildJob> InvokeAsync(
        SourceBuildJob job,
        SourceBuildRequestedEvent requestedEvent,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(job.Id, requestedEvent.BuildJobId, StringComparison.Ordinal) ||
            !string.Equals(job.TenantId, requestedEvent.EventCore.TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A source build request event must preserve build-job and tenant identity.");
        }

        return transactions.InvokeOutcomeAsync<SourceBuildJob>(async (transaction, ct) =>
        {
            await jobs.InsertOneAsync(transaction.Session, job, cancellationToken: ct);
            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(requestedEvent)],
                ct);
            return transaction.Commit(job);
        }, cancellationToken);
    }
}
