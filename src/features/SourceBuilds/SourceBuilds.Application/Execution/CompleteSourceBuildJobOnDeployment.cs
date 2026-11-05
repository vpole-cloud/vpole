using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Execution;

internal sealed class MongoCompleteSourceBuildJobOnDeployment(
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task InvokeAsync(
        SourceBuildDeploymentCompletion completion,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<Unit>(async (transaction, ct) =>
            {
                var job = await jobs
                    .Find(transaction.Session,
                        SourceBuildJobFilters.DeployingRelease(completion.TenantId, completion.ReleaseId))
                    .FirstOrDefaultAsync(ct);
                if (job is null)
                    return transaction.Commit(Unit.Value);

                var terminal = string.Equals(completion.ReleaseOutcome, "Succeeded", StringComparison.Ordinal)
                    ? job.Succeed(completion.CompletedAtUtc)
                    : job.Fail(SourceBuildPhase.Deploying,
                        "WebServiceDeploymentFailed",
                        completion.FailureMessage ?? "Web service deployment failed.",
                        completion.CompletedAtUtc);
                var replacement = await jobs.ReplaceOneAsync(
                    transaction.Session,
                    SourceBuildJobFilters.DeployingReleaseAtUpdatedAt(job),
                    terminal,
                    cancellationToken: ct);
                if (replacement.ModifiedCount != 1)
                    return transaction.Commit(Unit.Value);

                await storeOutboxWrites(transaction.Session, [OutboxWrite.Create(terminal.ToStateChangedEvent())], ct);
                return transaction.Commit(Unit.Value);
            },
            cancellationToken);
}
