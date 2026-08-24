using Microsoft.Extensions.Logging;
using Pole.Features.ContainerRegistries.Application.Persistence;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public sealed record RegistryProvisioningAttempt(
    string AttemptId,
    string ProjectName,
    string ExternalProjectId,
    string BuildRobotName,
    string PullRobotName,
    string? BuildExternalRobotId,
    string? PullExternalRobotId,
    DateTime? NameVerificationNotBeforeUtc);

public sealed record RegistryProvisioningFailure(string Code, string Message)
{
    public static RegistryProvisioningFailure Interrupted(string message) =>
        new("container-registries.provisioning.interrupted", message);
}

internal static class RegistryProvisioningCleanupPolicy
{
    internal static readonly TimeSpan NameVerificationHorizon = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan LeasedWorkTimeout = TimeSpan.FromMinutes(4);
}

/// <summary>
/// Reconciles the durable cleanup state machine. It is intentionally a cohesive concrete
/// Hangfire job rather than a broad data-access interface.
/// </summary>
public sealed class RegistryProvisioningCleanupService(
    RegistryProvisioningPersistence persistence,
    IContainerRegistryClient registryClient,
    TimeProvider timeProvider,
    ILogger<RegistryProvisioningCleanupService> logger)
{
    private const int ReconciliationBatchSize = 100;
    private static readonly TimeSpan AbandonedAttemptAge = TimeSpan.FromMinutes(10);

    public Task<CleanupRequiredRegistryProjectProvisioning?> ClaimCleanupAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        RegistryProvisioningFailure originalFailure,
        CancellationToken cancellationToken = default) =>
        persistence.ClaimCleanupAsync(projectId, attempt, originalFailure, cancellationToken);

    public async Task<Result<Unit, ContainerRegistryProviderError>> CleanupAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning cleanup,
        CancellationToken cancellationToken = default)
    {
        var leasedCleanup = await persistence.TryAcquireCleanupLeaseAsync(projectId, cleanup, cancellationToken);
        if (leasedCleanup is null)
            return new Ok<Unit>(Unit.Value);

        using var workTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        workTimeout.CancelAfter(RegistryProvisioningCleanupPolicy.LeasedWorkTimeout);
        var result = await registryClient.DeleteRobotCredentialsAsync(
            [
                new(leasedCleanup.ProjectName, leasedCleanup.BuildRobotName, leasedCleanup.BuildExternalRobotId),
                new(leasedCleanup.ProjectName, leasedCleanup.PullRobotName, leasedCleanup.PullExternalRobotId)
            ],
            workTimeout.Token);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var requiresNameVerification = leasedCleanup.BuildExternalRobotId is null ||
                                       leasedCleanup.PullExternalRobotId is null;
        var cleanupResultValue = TypedResultExtensions.TryGetValue(result);
        if (!cleanupResultValue.Succeeded)
        {
            await persistence.RecordCleanupRetryAsync(
                projectId, leasedCleanup, ProviderFailure(cleanupResultValue.Error!), requiresNameVerification,
                now, cancellationToken);
            logger.LogWarning(
                "Container registry robot cleanup remains required for project {RegistryProjectId}, Harbor project {ExternalProjectId}, attempt {AttemptId}.",
                projectId, leasedCleanup.ExternalProjectId, leasedCleanup.AttemptId);
            return new Err<ContainerRegistryProviderError>(cleanupResultValue.Error!);
        }
        var cleanupResult = cleanupResultValue.Value!;
        if (requiresNameVerification &&
            (cleanupResult.MatchedByNameCount > 0 || now < leasedCleanup.CleanupVerificationNotBeforeUtc))
        {
            await persistence.RecordCleanupVerificationPendingAsync(
                projectId, leasedCleanup, cleanupResult.MatchedByNameCount, now, cancellationToken);
            return new Ok<Unit>(Unit.Value);
        }

        await persistence.MarkCleanupCompletedAsync(projectId, leasedCleanup, now, cancellationToken);
        logger.LogInformation(
            "Removed robot credentials from interrupted container registry provisioning for project {RegistryProjectId}, Harbor project {ExternalProjectId}, attempt {AttemptId}.",
            projectId, leasedCleanup.ExternalProjectId, leasedCleanup.AttemptId);
        return new Ok<Unit>(Unit.Value);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var project in await persistence.LoadPendingCleanupAsync(now, ReconciliationBatchSize, cancellationToken))
        {
            if (project.State is CleanupRequiredRegistryProjectProvisioning cleanup)
                await ReconcileCleanupAsync(project.Id, cleanup, cancellationToken);
        }

        var cutoff = now - AbandonedAttemptAge;
        foreach (var project in await persistence.LoadAbandonedRemoteAsync(cutoff, ReconciliationBatchSize, cancellationToken))
        {
            if (project.State is RemoteProvisionedRegistryProject remote)
                await ReconcileAbandonedAsync(project, remote, cancellationToken);
        }

        foreach (var project in await persistence.LoadAbandonedProvisioningAsync(cutoff, ReconciliationBatchSize, cancellationToken))
        {
            if (project.State is ProvisioningRegistryProject provisioning)
                await ReconcileAbandonedProvisioningAsync(project, provisioning, cancellationToken);
        }
    }

    private async Task ReconcileAbandonedAsync(
        RegistryProject project,
        RemoteProvisionedRegistryProject remote,
        CancellationToken cancellationToken)
    {
        var names = RegistryRobotNames.ForAttempt(remote.AttemptId);
        var attempt = new RegistryProvisioningAttempt(
            remote.AttemptId, project.Name, remote.ExternalProjectId, names.Build, names.Pull,
            remote.BuildExternalRobotId, remote.PullExternalRobotId, null);
        await ClaimAndReconcileAsync(
            project.Id,
            attempt,
            RegistryProvisioningFailure.Interrupted(
                "A stale remote-provisioned attempt was queued for robot credential cleanup."),
            cancellationToken);
    }

    private async Task ReconcileAbandonedProvisioningAsync(
        RegistryProject project,
        ProvisioningRegistryProject provisioning,
        CancellationToken cancellationToken)
    {
        var names = RegistryRobotNames.ForAttempt(provisioning.AttemptId);
        var attempt = new RegistryProvisioningAttempt(
            provisioning.AttemptId, project.Name, project.Name, names.Build, names.Pull,
            null, null,
            provisioning.StartedAtUtc + RegistryProvisioningCleanupPolicy.NameVerificationHorizon);
        await ClaimAndReconcileAsync(
            project.Id,
            attempt,
            RegistryProvisioningFailure.Interrupted(
                "A stale provisioning attempt was queued for deterministic robot cleanup."),
            cancellationToken);
    }

    private async Task ClaimAndReconcileAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        RegistryProvisioningFailure failure,
        CancellationToken cancellationToken)
    {
        try
        {
            var cleanup = await ClaimCleanupAsync(projectId, attempt, failure, cancellationToken);
            if (cleanup is not null)
                await ReconcileCleanupAsync(projectId, cleanup, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Container registry provisioning reconciliation threw {ExceptionType} for project {RegistryProjectId}, attempt {AttemptId}; durable cleanup state was retained.",
                exception.GetType().Name, projectId, attempt.AttemptId);
        }
    }

    private async Task ReconcileCleanupAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning cleanup,
        CancellationToken cancellationToken)
    {
        try
        {
            await CleanupAsync(projectId, cleanup, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Container registry robot cleanup reconciliation threw {ExceptionType} for project {RegistryProjectId}, Harbor project {ExternalProjectId}, attempt {AttemptId}; durable cleanup state was retained.",
                exception.GetType().Name, projectId, cleanup.ExternalProjectId, cleanup.AttemptId);
        }
    }

    private static RegistryProvisioningFailure ProviderFailure(ContainerRegistryProviderError error) =>
        error switch
        {
            ContainerRegistryProviderNotConfigured => new(
                "container-registries.provider.not-configured", "Harbor is not configured."),
            ContainerRegistryProviderUnavailable => new(
                "container-registries.provider.unavailable", "Harbor is unavailable.")
        };
}
