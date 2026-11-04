using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public union QueueSourceBuildJobOutcome(
    SourceBuildJob,
    SourceBuildWebServiceNotFound);

public delegate Task<QueueSourceBuildJobOutcome> QueueSourceBuildJob(
    SourceBuildJob job,
    SourceBuildRequestedEvent requestedEvent,
    CancellationToken cancellationToken = default);

internal sealed class MongoQueueSourceBuildJob(
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    AdmitSourceBuildWebService admitWebService,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<QueueSourceBuildJobOutcome> InvokeAsync(
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

        return transactions.InvokeOutcomeAsync<QueueSourceBuildJobOutcome>(
            async (transaction, ct) =>
            {
                var admitted = await admitWebService(
                    new AdmitSourceBuildWebServiceRequest(
                        TenantId: job.TenantId,
                        WebServiceId: job.WebServiceId),
                    session: transaction.Session,
                    cancellationToken: ct);
                if (admitted is SourceBuildWebServiceNotFound error)
                {
                    QueueSourceBuildJobOutcome outcome = error;
                    return transaction.Abort(outcome);
                }

                await jobs.InsertOneAsync(transaction.Session, job, cancellationToken: ct);
                await storeOutboxWrites(
                    transaction.Session,
                    [
                        OutboxWrite.Create(requestedEvent),
                        OutboxWrite.Create(job.ToStateChangedEvent() with { EventCore = requestedEvent.EventCore })
                    ],
                    ct);
                QueueSourceBuildJobOutcome accepted = job;
                return transaction.Commit(accepted);
            },
            cancellationToken);
    }
}
