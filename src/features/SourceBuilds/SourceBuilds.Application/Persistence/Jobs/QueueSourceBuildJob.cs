using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public delegate Task<Result<SourceBuildJob, SourceBuildWebServiceNotFound>> QueueSourceBuildJob(
    SourceBuildJob job,
    SourceBuildRequestedEvent requestedEvent,
    CancellationToken cancellationToken = default);

internal sealed class MongoQueueSourceBuildJob(
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    AdmitSourceBuildWebService admitWebService,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<Result<SourceBuildJob, SourceBuildWebServiceNotFound>> InvokeAsync(
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

        return transactions.InvokeOutcomeAsync<Result<SourceBuildJob, SourceBuildWebServiceNotFound>>(
            async (transaction, ct) =>
            {
                var admitted = await admitWebService(
                    job.TenantId,
                    job.WebServiceId,
                    transaction.Session,
                    ct);
                if (!admitted.TryGetValue(out _, out var error))
                    return transaction.Abort(new Err<SourceBuildWebServiceNotFound>(error));

                await jobs.InsertOneAsync(transaction.Session, job, cancellationToken: ct);
                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(requestedEvent)],
                    ct);
                return transaction.Commit(new Ok<SourceBuildJob>(job));
            },
            cancellationToken);
    }
}
