using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public sealed record SourceBuildDeploymentCompletion(
    string TenantId,
    string ReleaseId,
    string ReleaseOutcome,
    string? FailureMessage,
    DateTime CompletedAtUtc);

public delegate Task CompleteSourceBuildJobOnDeployment(
    SourceBuildDeploymentCompletion completion,
    CancellationToken cancellationToken = default);

internal sealed class MongoCompleteSourceBuildJobOnDeployment(
    IMongoCollection<SourceBuildJob> jobs)
{
    public async Task InvokeAsync(
        SourceBuildDeploymentCompletion completion,
        CancellationToken cancellationToken = default)
    {
        var job = await jobs
            .Find(SourceBuildJobFilters.DeployingRelease(completion.TenantId, completion.ReleaseId))
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
            return;

        var terminal = string.Equals(
            completion.ReleaseOutcome,
            "Succeeded",
            StringComparison.Ordinal)
            ? job.Succeed(completion.CompletedAtUtc)
            : job.Fail(
                SourceBuildPhase.Deploying,
                "WebServiceDeploymentFailed",
                completion.FailureMessage ?? "Web service deployment failed.",
                completion.CompletedAtUtc);
        await jobs.ReplaceOneAsync(
            SourceBuildJobFilters.DeployingReleaseAtUpdatedAt(job),
            terminal,
            cancellationToken: cancellationToken);
    }
}
