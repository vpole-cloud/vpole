using Pole.Features.SourceBuilds.Application.Mongo;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Execution.Queueing;

public union QueueSourceBuildJobOutcome(
    SourceBuildJob,
    SourceBuildWebServiceNotFound,
    SourceBuildConfigurationUpdateConflict,
    SourceBuildConfigurationRepositoryChanged);

public delegate Task<QueueSourceBuildJobOutcome> QueueSourceBuildJob(
    SourceBuildJob job,
    SourceBuildRequestedEvent requestedEvent,
    CancellationToken cancellationToken = default);

internal sealed class MongoQueueSourceBuildJob(
    IMongoCollection<SourceBuildJob> jobs,
    IMongoCollection<SourceBuildConfiguration> configurations,
    FenceSourceRepository fenceRepository,
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
                if (await fenceRepository(new(job.TenantId, job.SourceRepositoryId,
                    job.InputVersion.RepositoryRevision), transaction.Session, ct) is not SourceRepositoryFenced)
                    return transaction.Abort(new SourceBuildConfigurationRepositoryChanged(job.SourceRepositoryId));

                var updated = await configurations.UpdateOneAsync(transaction.Session,
                    SourceBuildConfigurationFilters.ForQueue(job),
                    Builders<SourceBuildConfiguration>.Update.Inc(item => item.BuildGeneration, 1)
                        .Inc(item => item.ReferenceFence, 1), cancellationToken: ct);
                if (updated.MatchedCount != 1)
                    return transaction.Abort(new SourceBuildConfigurationUpdateConflict(job.ConfigurationId));

                var admitted = await admitWebService(
                    new AdmitSourceBuildWebServiceRequest(
                        TenantId: job.TenantId,
                        WebServiceId: job.WebServiceId,
                        ConfigurationId: job.ConfigurationId,
                        Purpose: SourceBuildAdmissionPurpose.Build),
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
