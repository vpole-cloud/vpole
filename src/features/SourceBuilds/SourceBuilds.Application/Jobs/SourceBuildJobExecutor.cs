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
    CreateSourceBuildWorkspace createSourceBuildWorkspace,
    PrepareRegistryBuildPush prepareRegistryBuildPush,
    BuildContainerImage buildContainerImage,
    GetTenantRegistryArtifact getTenantRegistryArtifact,
    DeploySourceBuildWebService deploySourceBuildWebService,
    TryPersistSourceBuildJob tryPersistSourceBuildJob,
    TimeProvider timeProvider,
    ILogger<SourceBuildJobExecutor> logger,
    CreateSourceBuildLogCapture createLogCapture,
    RenewSourceBuildExecution renewExecution,
    CheckSourceRepositoryBuildAccess checkRepositoryAccess)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(string buildJobId)
    {
        var job = await claimQueuedSourceBuildJob(
            new(buildJobId, Guid.NewGuid().ToString("N")));
        if (job is null)
            return;

        var execution = (ClaimedSourceBuildExecution)job.Execution;
        var trigger = (ManualSourceBuildTrigger)job.Trigger;
        await using var heartbeat = new SourceBuildHeartbeat(
            new(job.TenantId, job.Id, execution.WorkerId, execution.Attempt), renewExecution, timeProvider);
        var cancellationToken = heartbeat.Token;
        await using var capture = await createLogCapture(job);
        await capture.WriteAsync(SourceBuildLogStream.System,
            "Source build started. Repository archive preparation is in progress.\n");

        try
        {
            var configuration = await loadSourceBuildConfiguration(
                job.TenantId,
                job.ConfigurationId, cancellationToken);
            if (configuration is null)
            {
                await FailAsync(
                    job,
                    SourceBuildPhase.Cloning,
                    "ConfigurationNotFound",
                    "The source build configuration no longer exists.");
                return;
            }

            if (configuration.Revision != job.InputVersion.ConfigurationRevision ||
                configuration.BuildGeneration != job.InputVersion.Generation)
            {
                await FailAsync(job, SourceBuildPhase.Cloning, "SupersededBuild",
                    "A newer build request or configuration superseded this build.");
                return;
            }

            var workspaceResult = await createSourceBuildWorkspace(
                job.TenantId,
                job.SourceRepositoryId,
                job.CommitSha,
                job.Id, cancellationToken);
            if (workspaceResult is CreateSourceBuildWorkspaceError workspaceError)
            {
                await FailAsync(job, SourceBuildPhase.Cloning, workspaceError);
                return;
            }

            var workspaceValue = (SourceBuildWorkspace)workspaceResult.Value;
            await using (workspaceValue)
            {
                var analyzing = job.StartAnalyzing(timeProvider.GetUtcNow().UtcDateTime);
                if (!await tryPersistSourceBuildJob(job, analyzing, cancellationToken))
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
                var building = job.StartBuilding(timeProvider.GetUtcNow().UtcDateTime);
                if (!await tryPersistSourceBuildJob(job, building, cancellationToken))
                    return;
                job = building;
                await capture.WriteAsync(SourceBuildLogStream.System,
                    "BuildKit build and registry push are starting.\n");

                var repositoryName = $"web-services/{job.WebServiceId}";
                var tag = $"build-{job.Id}-attempt-{execution.Attempt}";
                var registryAccessResult = await prepareRegistryBuildPush(
                    new PrepareRegistryBuildPushRequest(
                        TenantId: job.TenantId,
                        InitiatedByUserId: trigger.UserId,
                        RepositoryName: repositoryName,
                        Tag: tag), cancellationToken);
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
                        registryAccess.Secret,
                        capture.WriteAsync,
                        capture.MarkUnavailable), cancellationToken);
                if (imageResult is BuildContainerImageError imageError)
                {
                    await capture.WriteAsync(SourceBuildLogStream.System,
                        "BuildKit did not produce an accepted image. Inspect the job's fixed failure summary.\n");
                    await FailAsync(job, SourceBuildPhase.Building, imageError);
                    return;
                }
                var image = (BuiltContainerImage)imageResult.Value;
                await capture.WriteAsync(SourceBuildLogStream.System,
                    "BuildKit output capture finished; deployment outcome is tracked separately.\n");

                var pushing = job.StartPushing(
                    image.Reference,
                    image.Digest,
                    timeProvider.GetUtcNow().UtcDateTime);
                if (!await tryPersistSourceBuildJob(job, pushing, cancellationToken))
                    return;
                job = pushing;

                var artifactResult = await getTenantRegistryArtifact(
                    new GetTenantRegistryArtifactRequest(
                        TenantId: job.TenantId,
                        RegistryProjectId: registryAccess.RegistryProjectId,
                        RepositoryName: repositoryName,
                        Reference: tag), cancellationToken);
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

                var access = await checkRepositoryAccess(new(job.TenantId, job.SourceRepositoryId), cancellationToken);
                if (access is not SourceRepositoryBuildSnapshot repository)
                {
                    var failure = RepositoryAccessFailure(access);
                    await FailAsync(job, SourceBuildPhase.Pushing, failure.Code, failure.Message);
                    return;
                }

                if (repository.Id != job.SourceRepositoryId ||
                    repository.Revision != job.InputVersion.RepositoryRevision)
                {
                    await FailAsync(job, SourceBuildPhase.Pushing, "SourceRepositoryChanged",
                        "The application repository binding changed while the artifact was being built.");
                    return;
                }

                var deploymentResult = await deploySourceBuildWebService(
                    new SourceBuildDeploymentRequest(
                        job.TenantId,
                        job.WebServiceId,
                        job.Id,
                        job.SourceRepositoryId,
                        job.CommitSha,
                        new(job.ConfigurationId, job.InputVersion.ConfigurationRevision, job.InputVersion.Generation,
                            execution.WorkerId, execution.Attempt),
                        trigger.UserId,
                        trigger.Email,
                        new SourceBuildDeploymentImage(
                            registryAccess.Target.RegistryHost,
                            $"{registryAccess.Target.ProjectName}/{registryAccess.Target.RepositoryName}",
                            registryAccess.Target.Tag,
                            artifact.Digest,
                            registryAccess.KubernetesPullSecretName)), cancellationToken);
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
                await tryPersistSourceBuildJob(job, deploying, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Unexpected source build failure for {BuildJobId} ({ExceptionType}).",
                buildJobId,
                exception.GetType().FullName);
            await FailAsync(
                job, PhaseOf(job.State),
                "UnexpectedBuildFailure",
                "An unexpected error interrupted the source build.");
        }
    }

    private static (string Code, string Message) RepositoryAccessFailure(
        CheckSourceRepositoryBuildAccessOutcome outcome) =>
        outcome switch
        {
            SourceRepositoryBuildNotFound =>
                ("SourceRepositoryNotFound", "The application repository no longer exists."),
            SourceRepositoryBuildConnectionNotFound =>
                ("SourceRepositoryConnectionNotFound", "The application repository connection is unavailable."),
            SourceRepositoryRemoteNotFound =>
                ("SourceRepositoryRemoteNotFound", "The remote repository is unavailable."),
            SourceRepositoryAccessDenied =>
                ("SourceRepositoryAccessDenied", "Access to the application repository was revoked."),
            SourceRepositoryUnavailable => ("SourceRepositoryUnavailable", "Repository access could not be verified."),
            SourceRepositoryBuildSnapshot =>
                throw new InvalidOperationException("An available repository is not a failure.")
        };

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
                ("RegistryCredentialProtectionFailed",
                "The container registry build credential could not be protected.")
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
                ("WebServiceNoActiveDeployments",
                    $"Web service '{empty.WebServiceId}' has no active resource deployments."),
            SourceBuildWebServiceDeploymentRejected rejected =>
                (DeploymentRejectionCode(rejected.Reason),
                    $"Web service deployment was rejected ({rejected.Reason.Value.GetType().Name})."),
            SourceBuildWebServiceUpdateConflict conflict =>
                conflict.Reason switch
                {
                    SourceBuildWebServiceUpdateConflictReason.SupersededBuild =>
                        ("SupersededBuild", "A newer build request or configuration superseded this build."),
                    SourceBuildWebServiceUpdateConflictReason.GitOwnedConfiguration =>
                        ("GitOwnedConfiguration",
                    "The image was built; edit the managing Git source to change the workload image."),
                    SourceBuildWebServiceUpdateConflictReason.ExpectedRevisionConflict =>
                        ("WebServiceUpdateConflict",
                    $"Web service '{conflict.WebServiceId}' changed while the source build was being deployed."),
                    _ => throw new ArgumentOutOfRangeException(nameof(conflict.Reason))
                }
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

}
