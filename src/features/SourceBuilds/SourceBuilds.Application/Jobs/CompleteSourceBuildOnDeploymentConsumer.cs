using MassTransit;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class CompleteSourceBuildOnDeploymentConsumer(
    ISourceBuildDbContext dbContext,
    TimeProvider timeProvider)
    : IConsumer<WebServiceDeploymentCompletedEvent>
{
    public Task Consume(ConsumeContext<WebServiceDeploymentCompletedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        WebServiceDeploymentCompletedEvent deployment,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<SourceBuildJob>.Filter.And(
            Builders<SourceBuildJob>.Filter.Eq(item => item.TenantId, deployment.EventCore.TenantId),
            Builders<SourceBuildJob>.Filter.Eq(
                item => ((DeployingSourceBuild)item.State).ReleaseId,
                deployment.ReleaseId),
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, DeployingSourceBuild>(
                item => item.State));
        var job = await dbContext.SourceBuildJobs
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var terminal = deployment.Succeeded
            ? job.Succeed(now)
            : job.Fail(
                SourceBuildPhase.Deploying,
                "WebServiceDeploymentFailed",
                deployment.FailureMessage ?? "Web service deployment failed.",
                now);
        await dbContext.SourceBuildJobs.ReplaceOneAsync(
            Builders<SourceBuildJob>.Filter.And(
                Builders<SourceBuildJob>.Filter.Eq(item => item.Id, job.Id),
                Builders<SourceBuildJob>.Filter.Eq(item => item.UpdatedAtUtc, job.UpdatedAtUtc),
                Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, DeployingSourceBuild>(
                    item => item.State)),
            terminal,
            cancellationToken: cancellationToken);
    }
}
