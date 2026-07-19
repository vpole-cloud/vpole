using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public sealed record RegistryProvisioningAttempt(
    string AttemptId,
    string ExternalProjectId,
    string BuildExternalRobotId,
    string PullExternalRobotId);

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
            attempt.ExternalProjectId,
            attempt.BuildExternalRobotId,
            attempt.PullExternalRobotId,
            originalFailure.GetType().Name,
            originalFailure.Message,
            null,
            null,
            0,
            now,
            null);
        return await dbContext.RegistryProjects.FindOneAndUpdateAsync(
            Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
                Builders<RegistryProject>.Filter.Or(
                    Builders<RegistryProject>.Filter.And(
                        Builders<RegistryProject>.Filter.OfType<RegistryProjectState, ProvisioningRegistryProject>(
                            item => item.State),
                        Builders<RegistryProject>.Filter.Eq(
                            item => ((ProvisioningRegistryProject)item.State).AttemptId,
                            attempt.AttemptId)),
                    Builders<RegistryProject>.Filter.And(
                        Builders<RegistryProject>.Filter.OfType<RegistryProjectState, RemoteProvisionedRegistryProject>(
                            item => item.State),
                        Builders<RegistryProject>.Filter.Eq(
                            item => ((RemoteProvisionedRegistryProject)item.State).AttemptId,
                            attempt.AttemptId)))),
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
        var result = await registryClient.DeleteRobotCredentialsAsync(
            [cleanup.BuildExternalRobotId, cleanup.PullExternalRobotId],
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
                cleanup.AttemptId));

        if (result is Failure cleanupFailure)
        {
            var failedCleanup = cleanup with
            {
                LastCleanupFailureCode = cleanupFailure.GetType().Name,
                LastCleanupFailureMessage = cleanupFailure.Message,
                CleanupAttemptCount = cleanup.CleanupAttemptCount + 1,
                LastCleanupAttemptAtUtc = now
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

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var pending = await dbContext.RegistryProjects
            .Find(Builders<RegistryProject>.Filter.OfType<RegistryProjectState, CleanupRequiredRegistryProjectProvisioning>(
                item => item.State))
            .SortBy(item => item.UpdatedAtUtc)
            .Limit(ReconciliationBatchSize)
            .ToListAsync(cancellationToken);
        foreach (var project in pending)
        {
            if (project.State is CleanupRequiredRegistryProjectProvisioning cleanup)
                await ReconcileCleanupAsync(project.Id, cleanup, cancellationToken);
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime - AbandonedAttemptAge;
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
            await ReconcileAbandonedAsync(project.Id, remote, cancellationToken);
        }
    }

    private async Task ReconcileAbandonedAsync(
        string projectId,
        RemoteProvisionedRegistryProject remote,
        CancellationToken cancellationToken)
    {
        try
        {
            var attempt = new RegistryProvisioningAttempt(
                remote.AttemptId,
                remote.ExternalProjectId,
                remote.BuildExternalRobotId,
                remote.PullExternalRobotId);
            var claimed = await ClaimCleanupAsync(
                projectId,
                attempt,
                Failures.Internal(
                    "Container registry provisioning was interrupted.",
                    "A stale remote-provisioned attempt was queued for robot credential cleanup."),
                cancellationToken);
            if (claimed is not null)
                await ReconcileCleanupAsync(projectId, claimed, cancellationToken);
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
                projectId,
                remote.ExternalProjectId,
                remote.AttemptId);
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
