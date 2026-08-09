using Hangfire;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Contracts;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class SourceBuildJobExecutor(
    ISourceBuildDbContext dbContext,
    ISourceBuildWorkspaceService workspaceService,
    IContainerRegistryBuildService registryBuildService,
    IBuildKitClient buildKit,
    IWebServiceSourceBuildDeploymentService deploymentService,
    TimeProvider timeProvider,
    ILogger<SourceBuildJobExecutor> logger) : ISourceBuildJobExecutor
{
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(string buildJobId)
    {
        var job = await ClaimAsync(buildJobId);
        if (job is null)
            return;

        try
        {
            var configuration = await dbContext.SourceBuildConfigurations
                .Find(item => item.TenantId == job.TenantId && item.Id == job.ConfigurationId)
                .FirstOrDefaultAsync();
            if (configuration is null)
            {
                await FailAsync(job, SourceBuildPhase.Cloning, "ConfigurationNotFound",
                    "The source build configuration no longer exists.");
                return;
            }

            var workspaceResult = await workspaceService.CreateAsync(
                job.TenantId,
                job.SourceRepositoryId,
                job.CommitSha,
                job.Id);
            if (!workspaceResult.TryGetValue(out var workspace, out var workspaceFailure))
            {
                await FailAsync(job, SourceBuildPhase.Cloning, workspaceFailure);
                return;
            }

            await using (workspace)
            {
                var analyzing = job.StartAnalyzing(timeProvider.GetUtcNow().UtcDateTime);
                if (!await ReplaceAsync(job, analyzing))
                    return;
                job = analyzing;

                if (string.IsNullOrWhiteSpace(job.Recipe.Dockerfile))
                {
                    await FailAsync(job, SourceBuildPhase.Analyzing, "DockerfileMissing",
                        "The selected build recipe does not contain a Dockerfile.");
                    return;
                }
                var manifestPath = Path.GetFullPath(Path.Combine(
                    workspace.SourcePath,
                    job.Recipe.ManifestPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!manifestPath.StartsWith(
                        Path.GetFullPath(workspace.SourcePath) + Path.DirectorySeparatorChar,
                        PathComparison) ||
                    !File.Exists(manifestPath))
                {
                    await FailAsync(job, SourceBuildPhase.Analyzing, "BuildManifestMissing",
                        "The selected build manifest is missing at the requested commit.");
                    return;
                }

                var building = job.StartBuilding(timeProvider.GetUtcNow().UtcDateTime);
                if (!await ReplaceAsync(job, building))
                    return;
                job = building;

                var actorUserId = (job.Trigger as ManualSourceBuildTrigger)?.UserId ?? "source-build-worker";
                var repositoryName = $"web-services/{job.WebServiceId}";
                var tag = $"build-{job.Id}";
                var registryAccessResult = await registryBuildService.PreparePushAsync(
                    job.TenantId,
                    actorUserId,
                    repositoryName,
                    tag);
                if (!registryAccessResult.TryGetValue(out var registryAccess, out var registryFailure))
                {
                    await FailAsync(job, SourceBuildPhase.Building, registryFailure);
                    return;
                }
                var imageResult = await buildKit.BuildAndPushAsync(
                    new BuildKitBuildRequest(
                        workspace.SourcePath,
                        job.Recipe.Dockerfile,
                        registryAccess.Target,
                        registryAccess.Username,
                        registryAccess.Secret));
                if (!imageResult.TryGetValue(out var builtImage, out var buildFailure))
                {
                    await FailAsync(job, SourceBuildPhase.Building, buildFailure);
                    return;
                }

                var pushing = job.StartPushing(builtImage.Reference, timeProvider.GetUtcNow().UtcDateTime);
                if (!await ReplaceAsync(job, pushing))
                    return;
                job = pushing;

                var artifactResult = await registryBuildService.GetArtifactAsync(
                    job.TenantId,
                    registryAccess.RegistryProjectId,
                    repositoryName,
                    tag);
                if (!artifactResult.TryGetValue(out var artifact, out var artifactFailure))
                {
                    await FailAsync(job, SourceBuildPhase.Pushing, artifactFailure);
                    return;
                }
                if (!string.Equals(artifact.Digest, builtImage.Digest, StringComparison.OrdinalIgnoreCase))
                {
                    await FailAsync(job, SourceBuildPhase.Pushing, "ImageDigestMismatch",
                        "The registry artifact digest does not match the BuildKit result.");
                    return;
                }

                var trigger = job.Trigger as ManualSourceBuildTrigger;
                var deploymentResult = await deploymentService.DeployAsync(
                    new SourceBuildDeploymentRequest(
                        job.TenantId,
                        job.WebServiceId,
                        job.Id,
                        job.SourceRepositoryId,
                        job.CommitSha,
                        trigger?.UserId,
                        trigger?.Email,
                        new SourceBuildDeploymentImage(
                            registryAccess.Target.RegistryHost,
                            $"{registryAccess.Target.ProjectName}/{registryAccess.Target.RepositoryName}",
                            registryAccess.Target.Tag,
                            artifact.Digest,
                            registryAccess.KubernetesPullSecretName)));
                if (!deploymentResult.TryGetValue(out var deployment, out var deploymentFailure))
                {
                    await FailAsync(job, SourceBuildPhase.Deploying, deploymentFailure);
                    return;
                }

                var deploying = job.StartDeploying(
                    artifact.Digest,
                    deployment.ReleaseId,
                    timeProvider.GetUtcNow().UtcDateTime);
                await ReplaceAsync(job, deploying);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Unexpected source build failure for {BuildJobId}: {ExceptionType}",
                buildJobId,
                exception.GetType().FullName);
            await FailCurrentAsync(buildJobId, "UnexpectedBuildFailure",
                "An unexpected error interrupted the source build.");
        }
    }

    private async Task<SourceBuildJob?> ClaimAsync(string buildJobId)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await dbContext.SourceBuildJobs.FindOneAndUpdateAsync(
            Builders<SourceBuildJob>.Filter.And(
                Builders<SourceBuildJob>.Filter.Eq(item => item.Id, buildJobId),
                Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, QueuedSourceBuild>(
                    item => item.State)),
            Builders<SourceBuildJob>.Update
                .Set(item => item.State, new CloningSourceBuild(now))
                .Set(item => item.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<SourceBuildJob> { ReturnDocument = ReturnDocument.After });
    }

    private Task<bool> ReplaceAsync(SourceBuildJob current, SourceBuildJob next) =>
        ReplaceCoreAsync(current.Id, current.UpdatedAtUtc, next);

    private async Task<bool> ReplaceCoreAsync(
        string buildJobId,
        DateTime expectedUpdatedAtUtc,
        SourceBuildJob next)
    {
        var replaced = await dbContext.SourceBuildJobs.ReplaceOneAsync(
            item => item.Id == buildJobId && item.UpdatedAtUtc == expectedUpdatedAtUtc,
            next);
        return replaced.ModifiedCount == 1;
    }

    private Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        Failure failure) =>
        FailAsync(job, phase, failure.GetType().Name, failure.Message);

    private async Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        string code,
        string message)
    {
        var failed = job.Fail(phase, code, message, timeProvider.GetUtcNow().UtcDateTime);
        await ReplaceAsync(job, failed);
    }

    private async Task FailCurrentAsync(string buildJobId, string code, string message)
    {
        var job = await dbContext.SourceBuildJobs
            .Find(item => item.Id == buildJobId)
            .FirstOrDefaultAsync();
        if (job is null || job.State is SucceededSourceBuild or FailedSourceBuild)
            return;
        await FailAsync(job, PhaseOf(job.State), code, message);
    }

    private static SourceBuildPhase PhaseOf(SourceBuildJobState state) => state switch
    {
        QueuedSourceBuild => SourceBuildPhase.Queued,
        CloningSourceBuild => SourceBuildPhase.Cloning,
        AnalyzingSourceBuild => SourceBuildPhase.Analyzing,
        BuildingSourceBuild => SourceBuildPhase.Building,
        PushingSourceBuild => SourceBuildPhase.Pushing,
        DeployingSourceBuild => SourceBuildPhase.Deploying,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
