using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
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

internal static class RegistryProvisioningCleanupPolicy
{
    public static readonly TimeSpan NameVerificationHorizon = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LeasedWorkTimeout = TimeSpan.FromMinutes(4);
}

public interface IRegistryProvisioningCleanupService
{
    Task<CleanupRequiredRegistryProjectProvisioning?> ClaimCleanupAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        Failure originalFailure,
        CancellationToken cancellationToken = default);

    Task<Result> CleanupAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning cleanup,
        CancellationToken cancellationToken = default);

    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

public sealed class RegistryProvisioningCleanupService(
    IContainerRegistryDbContext dbContext,
    IContainerRegistryClient registryClient,
    TimeProvider timeProvider,
    ILogger<RegistryProvisioningCleanupService> logger) : IRegistryProvisioningCleanupService
{
    private const int ReconciliationBatchSize = 100;
    private static readonly TimeSpan AbandonedAttemptAge = TimeSpan.FromMinutes(10);

    public async Task<CleanupRequiredRegistryProjectProvisioning?> ClaimCleanupAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        Failure originalFailure,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cleanup = new CleanupRequiredRegistryProjectProvisioning(
            attempt.AttemptId,
            attempt.ProjectName,
            attempt.ExternalProjectId,
            attempt.BuildRobotName,
            attempt.PullRobotName,
            attempt.BuildExternalRobotId,
            attempt.PullExternalRobotId,
            originalFailure.GetType().Name,
            originalFailure.Message,
            null,
            null,
            0,
            now,
            attempt.NameVerificationNotBeforeUtc is null || attempt.NameVerificationNotBeforeUtc <= now
                ? now
                : now + RegistryProvisioningCleanupPolicy.RetryDelay,
            attempt.NameVerificationNotBeforeUtc ?? now,
            null,
            null,
            null);
        var provisioningAttempt = Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.OfType<RegistryProjectState, ProvisioningRegistryProject>(
                item => item.State),
            Builders<RegistryProject>.Filter.Eq(
                item => ((ProvisioningRegistryProject)item.State).AttemptId,
                attempt.AttemptId));
        var expectedState = provisioningAttempt;
        if (attempt.NameVerificationNotBeforeUtc is null)
        {
            var remoteAttempt = Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.OfType<RegistryProjectState, RemoteProvisionedRegistryProject>(
                    item => item.State),
                Builders<RegistryProject>.Filter.Eq(
                    item => ((RemoteProvisionedRegistryProject)item.State).AttemptId,
                    attempt.AttemptId),
                Builders<RegistryProject>.Filter.Eq(
                    item => ((RemoteProvisionedRegistryProject)item.State).ExternalProjectId,
                    attempt.ExternalProjectId),
                Builders<RegistryProject>.Filter.Eq(
                    item => ((RemoteProvisionedRegistryProject)item.State).BuildExternalRobotId,
                    attempt.BuildExternalRobotId),
                Builders<RegistryProject>.Filter.Eq(
                    item => ((RemoteProvisionedRegistryProject)item.State).PullExternalRobotId,
                    attempt.PullExternalRobotId));
            expectedState = Builders<RegistryProject>.Filter.Or(provisioningAttempt, remoteAttempt);
        }

        return await dbContext.RegistryProjects.FindOneAndUpdateAsync(
            Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
                expectedState),
            Builders<RegistryProject>.Update
                .Set(item => item.State, cleanup)
                .Set(item => item.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<RegistryProject> { ReturnDocument = ReturnDocument.After },
            cancellationToken) is { State: CleanupRequiredRegistryProjectProvisioning claimed }
            ? claimed
            : null;
    }

    public async Task<Result> CleanupAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning cleanup,
        CancellationToken cancellationToken = default)
    {
        var leasedCleanup = await TryAcquireCleanupLeaseAsync(
            projectId,
            cleanup,
            cancellationToken);
        if (leasedCleanup is null)
            return Result.Success();

        using var workTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        workTimeout.CancelAfter(RegistryProvisioningCleanupPolicy.LeasedWorkTimeout);
        return await CleanupWithLeaseAsync(projectId, leasedCleanup, workTimeout.Token);
    }

    private async Task<Result> CleanupWithLeaseAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning cleanup,
        CancellationToken cancellationToken)
    {
        var result = await registryClient.DeleteRobotCredentialsAsync(
            [
                new RegistryRobotCleanupTarget(
                    cleanup.ProjectName,
                    cleanup.BuildRobotName,
                    cleanup.BuildExternalRobotId),
                new RegistryRobotCleanupTarget(
                    cleanup.ProjectName,
                    cleanup.PullRobotName,
                    cleanup.PullExternalRobotId)
            ],
            cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var currentState = Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
            Builders<RegistryProject>.Filter.OfType<RegistryProjectState, CleanupRequiredRegistryProjectProvisioning>(
                item => item.State),
            Builders<RegistryProject>.Filter.Eq(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupRequiredAtUtc,
                cleanup.CleanupRequiredAtUtc),
            Builders<RegistryProject>.Filter.Eq(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).AttemptId,
                cleanup.AttemptId),
            Builders<RegistryProject>.Filter.Eq(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseToken,
                cleanup.CleanupLeaseToken));

        if (!result.TryGetValue(out var cleanupResult, out var cleanupFailure))
        {
            var failedCleanup = cleanup with
            {
                LastCleanupFailureCode = cleanupFailure.GetType().Name,
                LastCleanupFailureMessage = cleanupFailure.Message,
                CleanupAttemptCount = cleanup.CleanupAttemptCount + 1,
                LastCleanupAttemptAtUtc = now,
                CleanupLeaseToken = null,
                CleanupLeaseExpiresAtUtc = null
            };
            await dbContext.RegistryProjects.UpdateOneAsync(
                currentState,
                Builders<RegistryProject>.Update
                    .Set(item => item.State, failedCleanup)
                    .Set(item => item.UpdatedAtUtc, now),
                cancellationToken: cancellationToken);
            logger.LogWarning(
                "Container registry robot cleanup remains required for project {RegistryProjectId}, Harbor project {ExternalProjectId}, attempt {AttemptId}: {FailureType}.",
                projectId,
                cleanup.ExternalProjectId,
                cleanup.AttemptId,
                cleanupFailure.GetType().Name);
            return cleanupFailure;
        }

        var requiresNameVerification = cleanup.BuildExternalRobotId is null || cleanup.PullExternalRobotId is null;
        if (requiresNameVerification &&
            (cleanupResult.MatchedByNameCount > 0 || now < cleanup.CleanupVerificationNotBeforeUtc))
        {
            var verificationNotBefore = cleanupResult.MatchedByNameCount > 0
                ? new[]
                {
                    cleanup.CleanupVerificationNotBeforeUtc,
                    now + RegistryProvisioningCleanupPolicy.RetryDelay
                }.Max()
                : cleanup.CleanupVerificationNotBeforeUtc;
            var pendingVerification = cleanup with
            {
                CleanupAttemptCount = cleanup.CleanupAttemptCount + 1,
                CleanupNotBeforeUtc = now + RegistryProvisioningCleanupPolicy.RetryDelay,
                CleanupVerificationNotBeforeUtc = verificationNotBefore,
                LastCleanupAttemptAtUtc = now,
                CleanupLeaseToken = null,
                CleanupLeaseExpiresAtUtc = null
            };
            await dbContext.RegistryProjects.UpdateOneAsync(
                currentState,
                Builders<RegistryProject>.Update
                    .Set(item => item.State, pendingVerification)
                    .Set(item => item.UpdatedAtUtc, now),
                cancellationToken: cancellationToken);
            return Result.Success();
        }

        await dbContext.RegistryProjects.UpdateOneAsync(
            currentState,
            Builders<RegistryProject>.Update
                .Set(item => item.State, new FailedRegistryProjectProvisioning(
                    cleanup.OriginalFailureCode,
                    cleanup.OriginalFailureMessage,
                    now))
                .Set(item => item.UpdatedAtUtc, now),
            cancellationToken: cancellationToken);
        logger.LogInformation(
            "Removed robot credentials from interrupted container registry provisioning for project {RegistryProjectId}, Harbor project {ExternalProjectId}, attempt {AttemptId}.",
            projectId,
            cleanup.ExternalProjectId,
            cleanup.AttemptId);
        return Result.Success();
    }

    private async Task<CleanupRequiredRegistryProjectProvisioning?> TryAcquireCleanupLeaseAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning observedCleanup,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseExpiresAt = now + RegistryProvisioningCleanupPolicy.LeaseDuration;
        var availableLease = Builders<RegistryProject>.Filter.Or(
            Builders<RegistryProject>.Filter.Eq(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseToken,
                null),
            Builders<RegistryProject>.Filter.Lte(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseExpiresAtUtc,
                now));
        var leasedProject = await dbContext.RegistryProjects.FindOneAndUpdateAsync(
            Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
                Builders<RegistryProject>.Filter.OfType<RegistryProjectState, CleanupRequiredRegistryProjectProvisioning>(
                    item => item.State),
                Builders<RegistryProject>.Filter.Eq(
                    item => ((CleanupRequiredRegistryProjectProvisioning)item.State).AttemptId,
                    observedCleanup.AttemptId),
                Builders<RegistryProject>.Filter.Eq(
                    item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupRequiredAtUtc,
                    observedCleanup.CleanupRequiredAtUtc),
                Builders<RegistryProject>.Filter.Lte(
                    item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupNotBeforeUtc,
                    now),
                availableLease),
            Builders<RegistryProject>.Update
                .Set(
                    item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseToken,
                    leaseToken)
                .Set(
                    item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseExpiresAtUtc,
                    leaseExpiresAt)
                .Set(item => item.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<RegistryProject> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        return leasedProject?.State is CleanupRequiredRegistryProjectProvisioning leasedCleanup &&
               leasedCleanup.CleanupLeaseToken == leaseToken
            ? leasedCleanup
            : null;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pending = await dbContext.RegistryProjects
            .Find(Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.OfType<RegistryProjectState, CleanupRequiredRegistryProjectProvisioning>(
                    item => item.State),
                Builders<RegistryProject>.Filter.Lte(
                    item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupNotBeforeUtc,
                    now)))
            .SortBy(item => item.UpdatedAtUtc)
            .Limit(ReconciliationBatchSize)
            .ToListAsync(cancellationToken);
        foreach (var project in pending)
        {
            if (project.State is CleanupRequiredRegistryProjectProvisioning cleanup)
                await ReconcileCleanupAsync(project.Id, cleanup, cancellationToken);
        }

        var cutoff = now - AbandonedAttemptAge;
        var abandoned = await dbContext.RegistryProjects
            .Find(Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.OfType<RegistryProjectState, RemoteProvisionedRegistryProject>(
                    item => item.State),
                Builders<RegistryProject>.Filter.Lte(item => item.UpdatedAtUtc, cutoff)))
            .SortBy(item => item.UpdatedAtUtc)
            .Limit(ReconciliationBatchSize)
            .ToListAsync(cancellationToken);
        foreach (var project in abandoned)
        {
            if (project.State is not RemoteProvisionedRegistryProject remote)
                continue;
            await ReconcileAbandonedAsync(project, remote, cancellationToken);
        }

        var abandonedProvisioning = await dbContext.RegistryProjects
            .Find(Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.OfType<RegistryProjectState, ProvisioningRegistryProject>(
                    item => item.State),
                Builders<RegistryProject>.Filter.Lte(item => item.UpdatedAtUtc, cutoff)))
            .SortBy(item => item.UpdatedAtUtc)
            .Limit(ReconciliationBatchSize)
            .ToListAsync(cancellationToken);
        foreach (var project in abandonedProvisioning)
        {
            if (project.State is not ProvisioningRegistryProject provisioning)
                continue;
            await ReconcileAbandonedProvisioningAsync(project, provisioning, cancellationToken);
        }
    }

    private async Task ReconcileAbandonedAsync(
        RegistryProject project,
        RemoteProvisionedRegistryProject remote,
        CancellationToken cancellationToken)
    {
        try
        {
            var names = RegistryRobotNames.ForAttempt(remote.AttemptId);
            var attempt = new RegistryProvisioningAttempt(
                remote.AttemptId,
                project.Name,
                remote.ExternalProjectId,
                names.Build,
                names.Pull,
                remote.BuildExternalRobotId,
                remote.PullExternalRobotId,
                null);
            var claimed = await ClaimCleanupAsync(
                project.Id,
                attempt,
                Failures.Internal(
                    "Container registry provisioning was interrupted.",
                    "A stale remote-provisioned attempt was queued for robot credential cleanup."),
                cancellationToken);
            if (claimed is not null)
                await ReconcileCleanupAsync(project.Id, claimed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Abandoned container registry provisioning reconciliation threw {ExceptionType} for project {RegistryProjectId}, Harbor project {ExternalProjectId}, attempt {AttemptId}.",
                exception.GetType().Name,
                project.Id,
                remote.ExternalProjectId,
                remote.AttemptId);
        }
    }

    private async Task ReconcileAbandonedProvisioningAsync(
        RegistryProject project,
        ProvisioningRegistryProject provisioning,
        CancellationToken cancellationToken)
    {
        try
        {
            var names = RegistryRobotNames.ForAttempt(provisioning.AttemptId);
            var attempt = new RegistryProvisioningAttempt(
                provisioning.AttemptId,
                project.Name,
                project.Name,
                names.Build,
                names.Pull,
                null,
                null,
                provisioning.StartedAtUtc + RegistryProvisioningCleanupPolicy.NameVerificationHorizon);
            var claimed = await ClaimCleanupAsync(
                project.Id,
                attempt,
                Failures.Internal(
                    "Container registry provisioning was interrupted.",
                    "A stale provisioning attempt was queued for deterministic robot cleanup."),
                cancellationToken);
            if (claimed is not null)
                await ReconcileCleanupAsync(project.Id, claimed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Abandoned container registry provisioning reconciliation threw {ExceptionType} for project {RegistryProjectId}, attempt {AttemptId}.",
                exception.GetType().Name,
                project.Id,
                provisioning.AttemptId);
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
                "Container registry robot cleanup reconciliation threw {ExceptionType} for project {RegistryProjectId}, Harbor project {ExternalProjectId}, attempt {AttemptId}; the durable cleanup state was retained.",
                exception.GetType().Name,
                projectId,
                cleanup.ExternalProjectId,
                cleanup.AttemptId);
        }
    }
}
