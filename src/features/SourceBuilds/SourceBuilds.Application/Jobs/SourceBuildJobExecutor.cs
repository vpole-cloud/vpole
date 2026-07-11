using Hangfire;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Contracts;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class SourceBuildJobExecutor(
    ISourceBuildDbContext dbContext,
    IGitProviderDbContext gitProviderDbContext,
    ISourceBuildWorkspaceService workspaceService,
    IContainerRegistryProvisioningService registryProvisioning,
    IRegistryCredentialResolver credentialResolver,
    IContainerRegistryClient registryClient,
    IBuildKitClient buildKit,
    IWebServiceSourceBuildDeploymentService deploymentService,
    HarborOptions harborOptions,
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

            var sourceRepository = await gitProviderDbContext.SourceRepositories
                .Find(item => item.TenantId == job.TenantId && item.Id == job.SourceRepositoryId)
                .FirstOrDefaultAsync();
            if (sourceRepository is null)
            {
                await FailAsync(job, SourceBuildPhase.Cloning, "SourceRepositoryNotFound",
                    "The linked source repository no longer exists.");
                return;
            }

            var connection = await gitProviderDbContext.GitProviderConnections
                .Find(item => item.TenantId == job.TenantId && item.Id == sourceRepository.ConnectionId)
                .FirstOrDefaultAsync();
            if (connection is null)
            {
                await FailAsync(job, SourceBuildPhase.Cloning, "GitProviderConnectionNotFound",
                    "The linked git provider connection no longer exists.");
                return;
            }

            var exactSource = sourceRepository with { Branch = job.Branch, CommitSha = job.CommitSha };
            var workspaceResult = await workspaceService.CreateAsync(connection, exactSource, job.Id);
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
                var registryProjectResult = await registryProvisioning.EnsureTenantProjectAsync(
                    job.TenantId,
                    actorUserId);
                if (!registryProjectResult.TryGetValue(out var registryProject, out var registryFailure))
                {
                    await FailAsync(job, SourceBuildPhase.Building, registryFailure);
                    return;
                }
                if (registryProject.State is not ReadyRegistryProject)
                {
                    await FailAsync(job, SourceBuildPhase.Building, "RegistryProjectNotReady",
                        "The tenant container registry is not ready.");
                    return;
                }

                var credentialResult = await credentialResolver.ResolveAsync(
                    job.TenantId,
                    registryProject.Id,
                    RegistryCredentialPurpose.BuildPushPull);
                if (!credentialResult.TryGetValue(out var credential, out var credentialFailure))
                {
                    await FailAsync(job, SourceBuildPhase.Building, credentialFailure);
                    return;
                }

                var repositoryName = $"web-services/{job.WebServiceId}";
                var tag = $"build-{job.Id}";
                var target = new RegistryPushTarget(
                    harborOptions.RegistryHost,
                    registryProject.Name,
                    repositoryName,
                    tag);
                var imageResult = await buildKit.BuildAndPushAsync(
                    new BuildKitBuildRequest(
                        workspace.SourcePath,
                        job.Recipe.Dockerfile,
                        target,
                        credential.Username,
                        credential.Secret));
                if (!imageResult.TryGetValue(out var builtImage, out var buildFailure))
                {
                    await FailAsync(job, SourceBuildPhase.Building, buildFailure);
                    return;
                }

                var pushing = job.StartPushing(builtImage.Reference, timeProvider.GetUtcNow().UtcDateTime);
                if (!await ReplaceAsync(job, pushing))
                    return;
                job = pushing;

                var artifactResult = await registryClient.GetArtifactAsync(
                    registryProject.Name,
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

                var pullSecretName = RuntimeRegistryAccessProvider.KubernetesSecretName(registryProject.Id);
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
                            target.RegistryHost,
                            $"{target.ProjectName}/{target.RepositoryName}",
                            target.Tag,
                            artifact.Digest,
                            pullSecretName)));
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
