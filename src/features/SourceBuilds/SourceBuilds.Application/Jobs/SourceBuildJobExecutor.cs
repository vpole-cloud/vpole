using Hangfire;
using Microsoft.Extensions.Logging;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Persistence;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Pole.Features.SourceBuilds.Application.Persistence.Jobs;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class SourceBuildJobExecutor(
    ClaimQueuedSourceBuildJob claimQueuedSourceBuildJob,
    LoadSourceBuildConfiguration loadSourceBuildConfiguration,
    LoadSourceBuildJobForExecution loadSourceBuildJobForExecution,
    CreateSourceBuildWorkspace createSourceBuildWorkspace,
    PrepareRegistryBuildPush prepareRegistryBuildPush,
    BuildContainerImage buildContainerImage,
    GetTenantRegistryArtifact getTenantRegistryArtifact,
    DeploySourceBuildWebService deploySourceBuildWebService,
    TryPersistSourceBuildJob tryPersistSourceBuildJob,
    TimeProvider timeProvider,
    ILogger<SourceBuildJobExecutor> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(string buildJobId)
    {
        var job = await claimQueuedSourceBuildJob(
            buildJobId,
            timeProvider.GetUtcNow().UtcDateTime);
        if (job is null)
            return;

        try
        {
            var configuration = await loadSourceBuildConfiguration(
                job.TenantId,
                job.ConfigurationId);
            if (configuration is null)
            {
                await FailAsync(
                    job,
                    SourceBuildPhase.Cloning,
                    "ConfigurationNotFound",
                    "The source build configuration no longer exists.");
                return;
            }

            var workspaceResult = await createSourceBuildWorkspace(
                job.TenantId,
                job.SourceRepositoryId,
                job.CommitSha,
                job.Id);
            if (workspaceResult is CreateSourceBuildWorkspaceError workspaceError)
            {
                await FailAsync(job, SourceBuildPhase.Cloning, workspaceError);
                return;
            }

            var workspaceValue = (SourceBuildWorkspace)workspaceResult.Value;
            await using (workspaceValue)
            {
                var analyzing = job.StartAnalyzing(timeProvider.GetUtcNow().UtcDateTime);
                if (!await tryPersistSourceBuildJob(job, analyzing))
                    return;
                job = analyzing;

                if (string.IsNullOrWhiteSpace(job.Recipe.Dockerfile))
                {
                    await FailAsync(
                        job,
                        SourceBuildPhase.Analyzing,
                        "DockerfileMissing",
                        "The selected build recipe does not contain a Dockerfile.");
                    return;
                }
                var manifestPath = Path.GetFullPath(Path.Combine(
                    workspaceValue.SourcePath,
                    job.Recipe.ManifestPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!manifestPath.StartsWith(
                        Path.GetFullPath(workspaceValue.SourcePath) + Path.DirectorySeparatorChar,
                        PathComparison) ||
                    !File.Exists(manifestPath))
                {
                    await FailAsync(
                        job,
                        SourceBuildPhase.Analyzing,
                        "BuildManifestMissing",
                        "The selected build manifest is missing at the requested commit.");
                    return;
                }

                var building = job.StartBuilding(timeProvider.GetUtcNow().UtcDateTime);
                if (!await tryPersistSourceBuildJob(job, building))
                    return;
                job = building;

                var actorUserId = (job.Trigger as ManualSourceBuildTrigger)?.UserId ?? "source-build-worker";
                var repositoryName = $"web-services/{job.WebServiceId}";
                var tag = $"build-{job.Id}";
                var registryAccessResult = await prepareRegistryBuildPush(
                    job.TenantId,
                    actorUserId,
                    repositoryName,
                    tag);
                if (registryAccessResult.Value is PrepareRegistryBuildPushError accessError)
                {
                    await FailAsync(job, SourceBuildPhase.Building, accessError);
                    return;
                }
                var registryAccess = (RegistryBuildAccess)registryAccessResult.Value;

                var imageResult = await buildContainerImage(
                    new BuildKitBuildRequest(
                        workspaceValue.SourcePath,
                        job.Recipe.Dockerfile,
                        registryAccess.Target,
                        registryAccess.Username,
                        registryAccess.Secret));
                if (imageResult is BuildContainerImageError imageError)
                {
                    await FailAsync(job, SourceBuildPhase.Building, imageError);
                    return;
                }
                var image = (BuiltContainerImage)imageResult.Value;

                var pushing = job.StartPushing(
                    image.Reference,
                    timeProvider.GetUtcNow().UtcDateTime);
                if (!await tryPersistSourceBuildJob(job, pushing))
                    return;
                job = pushing;

                var artifactResult = await getTenantRegistryArtifact(
                    job.TenantId,
                    registryAccess.RegistryProjectId,
                    repositoryName,
                    tag);
                if (artifactResult.Value is GetTenantRegistryArtifactError artifactError)
                {
                    await FailAsync(job, SourceBuildPhase.Pushing, artifactError);
                    return;
                }
                var artifact = (RegistryArtifact)artifactResult.Value;

                if (!string.Equals(
                        artifact.Digest,
                        image.Digest,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await FailAsync(
                        job,
                        SourceBuildPhase.Pushing,
                        "ImageDigestMismatch",
                        "The registry artifact digest does not match the BuildKit result.");
                    return;
                }

                var trigger = job.Trigger as ManualSourceBuildTrigger;
                var deploymentResult = await deploySourceBuildWebService(
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
                if (deploymentResult is SourceBuildWebServiceDeploymentError deploymentError)
                {
                    await FailAsync(job, SourceBuildPhase.Deploying, deploymentError);
                    return;
                }
                var deployment = (SourceBuildDeploymentResult)deploymentResult.Value;

                var deploying = job.StartDeploying(
                    artifact.Digest,
                    deployment.ReleaseId,
                    timeProvider.GetUtcNow().UtcDateTime);
                await tryPersistSourceBuildJob(job, deploying);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected source build failure for {BuildJobId}.",
                buildJobId);
            await FailCurrentAsync(
                buildJobId,
                "UnexpectedBuildFailure",
                "An unexpected error interrupted the source build.");
        }
    }

    private async Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        CreateSourceBuildWorkspaceError error) =>
        await FailAsync(job, phase, error switch
        {
            DownloadSourceRepositoryArchiveError download => DownloadFailure(download),
            SourceBuildRepositoryArchiveInvalid invalid =>
                ("RepositoryArchiveInvalid", invalid.Message),
            SourceBuildWorkspaceArchiveLimitExceeded =>
                ("RepositoryArchiveLimitExceeded", "Repository archive exceeds the configured download limit.")
        });

    private async Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        PrepareRegistryBuildPushError error) =>
        await FailAsync(job, phase, error switch
        {
            TenantContainerRegistryNotReady =>
                ("ContainerRegistryNotReady", "The tenant container registry is not ready."),
            TenantContainerRegistryProvisioningConflict =>
                ("ContainerRegistryProvisioningConflict", "The tenant container registry is being provisioned."),
            ContainerRegistryProviderError provider => provider switch
            {
                ContainerRegistryProviderNotConfigured =>
                    ("ContainerRegistryNotConfigured", "The container registry provider is not configured."),
                ContainerRegistryProviderUnavailable =>
                    ("ContainerRegistryUnavailable", "The container registry provider is unavailable.")
            },
            RegistryCredentialUnavailable =>
                ("RegistryCredentialUnavailable", "The container registry build credential is unavailable."),
            RegistryCredentialProtectionFailed =>
                ("RegistryCredentialProtectionFailed", "The container registry build credential could not be protected.")
        });

    private async Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        BuildContainerImageError error) =>
        await FailAsync(job, phase, error switch
        {
            BuildKitNotConfigured =>
                ("BuildKitNotConfigured", "Container image building is not configured."),
            BuildKitTimedOut timeout =>
                ("BuildKitTimedOut", $"BuildKit timed out after {timeout.TimeoutMinutes} minutes."),
            BuildKitUnavailable =>
                ("BuildKitUnavailable", "BuildKit could not be started."),
            BuildKitBuildRejected rejected =>
                ("BuildKitBuildRejected", rejected.Classification)
        });

    private async Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        GetTenantRegistryArtifactError error) =>
        await FailAsync(job, phase, error switch
        {
            TenantContainerRegistryProjectNotFound =>
                ("ContainerRegistryProjectNotFound", "The tenant container registry project was not found."),
            TenantContainerRegistryNotReady =>
                ("ContainerRegistryNotReady", "The tenant container registry is not ready."),
            ContainerRegistryArtifactLookupError lookup => lookup switch
            {
                ContainerRegistryArtifactNotFound =>
                    ("ContainerRegistryArtifactNotFound", "The pushed container image was not found in the registry."),
                ContainerRegistryProviderError provider => provider switch
                {
                    ContainerRegistryProviderNotConfigured =>
                        ("ContainerRegistryNotConfigured", "The container registry provider is not configured."),
                    ContainerRegistryProviderUnavailable =>
                        ("ContainerRegistryUnavailable", "The container registry provider is unavailable.")
                }
            }
        });

    private async Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        SourceBuildWebServiceDeploymentError error) =>
        await FailAsync(job, phase, error switch
        {
            SourceBuildWebServiceNotFound notFound =>
                ("WebServiceNotFound", $"Web service '{notFound.WebServiceId}' was not found."),
            SourceBuildWebServiceDeleting deleting =>
                ("WebServiceDeleting", $"Web service '{deleting.WebServiceId}' is being deleted."),
            SourceBuildWebServiceHasNoActiveDeployments empty =>
                ("WebServiceNoActiveDeployments", $"Web service '{empty.WebServiceId}' has no active resource deployments."),
            SourceBuildWebServiceDeploymentRejected rejected =>
                (DeploymentRejectionCode(rejected.Reason),
                    $"Web service deployment was rejected ({rejected.Reason.Value.GetType().Name})."),
            SourceBuildWebServiceUpdateConflict conflict =>
                ("WebServiceUpdateConflict", $"Web service '{conflict.WebServiceId}' changed while the source build was being deployed.")
        });

    private async Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        (string Code, string Message) failure) =>
        await FailAsync(job, phase, failure.Code, failure.Message);

    private async Task FailAsync(
        SourceBuildJob job,
        SourceBuildPhase phase,
        string code,
        string message)
    {
        var failed = job.Fail(phase, code, message, timeProvider.GetUtcNow().UtcDateTime);
        await tryPersistSourceBuildJob(job, failed);
    }

    private async Task FailCurrentAsync(string buildJobId, string code, string message)
    {
        var job = await loadSourceBuildJobForExecution(buildJobId);
        if (job is null || job.State is SucceededSourceBuild or FailedSourceBuild)
            return;
        await FailAsync(job, PhaseOf(job.State), code, message);
    }

    private static (string Code, string Message) DownloadFailure(
        DownloadSourceRepositoryArchiveError error) => error switch
        {
            SourceRepositoryBuildNotFound =>
                ("SourceRepositoryNotFound", "The linked source repository no longer exists."),
            SourceRepositoryBuildConnectionNotFound =>
                ("GitProviderConnectionNotFound", "The linked git provider connection no longer exists."),
            GitProviderRepositoryAccessError provider => provider switch
            {
                GitProviderUnsupported =>
                    ("GitProviderUnsupported", "The linked git provider is not supported."),
                GitProviderUnavailable =>
                    ("GitProviderUnavailable", "The linked git provider is unavailable.")
            }
        };

    private static string DeploymentRejectionCode(SourceBuildResourceDeploymentRejection error) =>
        error switch
        {
            SourceBuildResourceDeploymentNotFound => "ResourceDeploymentNotFound",
            SourceBuildResourceDeploymentChanged => "ResourceDeploymentChanged",
            SourceBuildResourceDeploymentCardinalityConflict => "ResourceDeploymentCardinalityConflict",
            SourceBuildResourceDeploymentPendingDeletion => "ResourceDeploymentPendingDeletion",
            SourceBuildResourceDeploymentPlacementUnavailable => "ResourceDeploymentPlacementUnavailable",
            SourceBuildResourceDeploymentReplacementTargetMismatch => "ResourceDeploymentReplacementTargetMismatch",
            SourceBuildResourceDeploymentIngressConfigurationChanged => "ResourceDeploymentIngressConfigurationChanged",
            SourceBuildResourceDeploymentRetryUnavailable => "ResourceDeploymentRetryUnavailable",
            SourceBuildResourceDeploymentComputeConfigurationChanged => "ResourceDeploymentComputeConfigurationChanged"
        };

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
