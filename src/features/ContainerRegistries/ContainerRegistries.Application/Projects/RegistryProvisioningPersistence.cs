using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Mongo;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Projects;

public union ClaimRegistryProvisioningOutcome(RegistryProject, TenantContainerRegistryProvisioningConflict);

public sealed record RemoteRegistryProvisioningRecorded;

public union RecordRemoteRegistryProvisioningOutcome(
    RemoteRegistryProvisioningRecorded,
    TenantContainerRegistryProvisioningConflict);

public union PersistReadyRegistryProjectOutcome(RegistryProject, TenantContainerRegistryProvisioningConflict);

/// <summary>
///     Owns the durable state transitions of the Harbor-project provisioning state machine.
///     Keeping these transitions together prevents orchestrators from bypassing its tenant and
///     concurrency fences.
/// </summary>
public sealed class RegistryProvisioningPersistence(
    IMongoCollection<RegistryProject> projects,
    IMongoCollection<RegistryCredential> credentials,
    MongoRunTransaction transactions,
    TimeProvider timeProvider
)
{
    internal async Task<ClaimRegistryProvisioningOutcome> ClaimProvisioningAsync(
        string tenantId,
        string initiatedByUserId,
        CancellationToken cancellationToken)
    {
        var existing = await projects.Find(ContainerRegistryFilters.HarborProjectByTenant(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing?.State is ReadyRegistryProject)
            return existing;
        if (existing?.State is not null and not FailedRegistryProjectProvisioning)
            return Conflict(tenantId);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (existing is not null)
        {
            var provisioning = new ProvisioningRegistryProject(Guid.NewGuid().ToString("N"), now);
            var claimed = await projects.FindOneAndUpdateAsync(
                ContainerRegistryFilters.FailedProvisioning(existing.Id),
                Builders<RegistryProject>.Update
                    .Set(item => item.State, provisioning)
                    .Set(item => item.UpdatedAtUtc, now),
                new FindOneAndUpdateOptions<RegistryProject> { ReturnDocument = ReturnDocument.After },
                cancellationToken);
            return claimed is null ? Conflict(tenantId) : claimed;
        }

        var project = RegistryProjectFactory.Create(
            tenantId,
            ContainerRegistryProvider.Harbor,
            RegistryProjectName.ForTenant(tenantId),
            initiatedByUserId,
            new RegistryProjectFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now));
        try
        {
            await projects.InsertOneAsync(project, cancellationToken: cancellationToken);
            return project;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Conflict(tenantId);
        }
    }

    internal async Task<RecordRemoteRegistryProvisioningOutcome> RecordRemoteProvisioningAsync(
        string projectId,
        RemoteProvisionedRegistryProject remoteState,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var update = await projects.UpdateOneAsync(
            ContainerRegistryFilters.ProvisioningAttempt(projectId, remoteState.AttemptId),
            Builders<RegistryProject>.Update
                .Set(item => item.State, remoteState)
                .Set(item => item.UpdatedAtUtc, remoteState.ProvisionedAtUtc),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == 1 ? new RemoteRegistryProvisioningRecorded() : ConflictUnit(tenantId);
    }

    internal Task<PersistReadyRegistryProjectOutcome> PersistReadyAsync(
        RegistryProject project,
        RegistryProvisioningAttempt attempt,
        ReadyRegistryProject ready,
        RegistryCredential buildCredential,
        RegistryCredential pullCredential,
        CancellationToken cancellationToken) => transactions.InvokeOutcomeAsync<PersistReadyRegistryProjectOutcome>(
        async (transaction, transactionCancellationToken) =>
        {
            var update = await projects.UpdateOneAsync(
                transaction.Session,
                ContainerRegistryFilters.RemoteProvisioningAttempt(project.Id, attempt),
                Builders<RegistryProject>.Update
                    .Set(item => item.State, ready)
                    .Set(item => item.UpdatedAtUtc, ready.ProvisionedAtUtc),
                cancellationToken: transactionCancellationToken);
            if (update.ModifiedCount != 1)
                return transaction.Abort(new TenantContainerRegistryProvisioningConflict(project.TenantId));

            await credentials.InsertManyAsync(
                transaction.Session,
                [buildCredential, pullCredential],
                cancellationToken: transactionCancellationToken);
            PersistReadyRegistryProjectOutcome outcome = project with
            {
                State = ready, UpdatedAtUtc = ready.ProvisionedAtUtc,
            };
            return transaction.Commit(outcome);
        },
        cancellationToken);

    internal async Task<RegistryProject?> LoadByIdAsync(string projectId, CancellationToken cancellationToken) =>
        await projects.Find(ContainerRegistryFilters.ProjectById(projectId)).FirstOrDefaultAsync(cancellationToken);

    internal async Task<CleanupRequiredRegistryProjectProvisioning?> FindMatchingCleanupAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        CancellationToken cancellationToken)
    {
        var project = await LoadByIdAsync(projectId, cancellationToken);
        return project?.State is CleanupRequiredRegistryProjectProvisioning cleanup &&
            cleanup.AttemptId == attempt.AttemptId && cleanup.ProjectName == attempt.ProjectName &&
            cleanup.ExternalProjectId == attempt.ExternalProjectId &&
            cleanup.BuildRobotName == attempt.BuildRobotName && cleanup.PullRobotName == attempt.PullRobotName &&
            cleanup.BuildExternalRobotId == attempt.BuildExternalRobotId &&
            cleanup.PullExternalRobotId == attempt.PullExternalRobotId
                ? cleanup
                : null;
    }

    internal async Task<CleanupRequiredRegistryProjectProvisioning?> ClaimCleanupAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        RegistryProvisioningFailure originalFailure,
        CancellationToken cancellationToken)
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
            originalFailure.Code,
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

        return await projects.FindOneAndUpdateAsync(
            ContainerRegistryFilters.CleanupClaimExpectedState(projectId, attempt),
            Builders<RegistryProject>.Update.Set(item => item.State, cleanup).Set(item => item.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<RegistryProject> { ReturnDocument = ReturnDocument.After },
            cancellationToken) is { State: CleanupRequiredRegistryProjectProvisioning claimed }
            ? claimed
            : null;
    }

    internal async Task<CleanupRequiredRegistryProjectProvisioning?> TryAcquireCleanupLeaseAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning observedCleanup,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseExpiresAt = now + RegistryProvisioningCleanupPolicy.LeaseDuration;
        var leasedProject = await projects.FindOneAndUpdateAsync(
            ContainerRegistryFilters.CleanupLeaseCandidate(projectId, observedCleanup, now),
            Builders<RegistryProject>.Update
                .Set(item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseToken, leaseToken)
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

    internal Task RecordCleanupRetryAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning cleanup,
        RegistryProvisioningFailure failure,
        bool requiresNameVerification,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var retryNotBefore = now + RegistryProvisioningCleanupPolicy.RetryDelay;
        var state = cleanup with
        {
            LastCleanupFailureCode = failure.Code,
            LastCleanupFailureMessage = failure.Message,
            CleanupAttemptCount = cleanup.CleanupAttemptCount + 1,
            CleanupNotBeforeUtc = retryNotBefore,
            CleanupVerificationNotBeforeUtc = requiresNameVerification
                ? new[] { cleanup.CleanupVerificationNotBeforeUtc, retryNotBefore }.Max()
                : cleanup.CleanupVerificationNotBeforeUtc,
            LastCleanupAttemptAtUtc = now,
            CleanupLeaseToken = null,
            CleanupLeaseExpiresAtUtc = null,
        };
        return projects.UpdateOneAsync(
            ContainerRegistryFilters.CleanupLease(projectId, cleanup),
            Builders<RegistryProject>.Update.Set(item => item.State, state).Set(item => item.UpdatedAtUtc, now),
            cancellationToken: cancellationToken);
    }

    internal Task RecordCleanupVerificationPendingAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning cleanup,
        int matchedByNameCount,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var retryNotBefore = now + RegistryProvisioningCleanupPolicy.RetryDelay;
        var verificationNotBefore = matchedByNameCount > 0
            ? new[] { cleanup.CleanupVerificationNotBeforeUtc, retryNotBefore }.Max()
            : cleanup.CleanupVerificationNotBeforeUtc;
        var state = cleanup with
        {
            CleanupAttemptCount = cleanup.CleanupAttemptCount + 1,
            CleanupNotBeforeUtc = retryNotBefore,
            CleanupVerificationNotBeforeUtc = verificationNotBefore,
            LastCleanupAttemptAtUtc = now,
            CleanupLeaseToken = null,
            CleanupLeaseExpiresAtUtc = null,
        };
        return projects.UpdateOneAsync(
            ContainerRegistryFilters.CleanupLease(projectId, cleanup),
            Builders<RegistryProject>.Update.Set(item => item.State, state).Set(item => item.UpdatedAtUtc, now),
            cancellationToken: cancellationToken);
    }

    internal Task MarkCleanupCompletedAsync(
        string projectId,
        CleanupRequiredRegistryProjectProvisioning cleanup,
        DateTime now,
        CancellationToken cancellationToken) => projects.UpdateOneAsync(
        ContainerRegistryFilters.CleanupLease(projectId, cleanup),
        Builders<RegistryProject>.Update
            .Set(
                item => item.State,
                new FailedRegistryProjectProvisioning(cleanup.OriginalFailureCode, cleanup.OriginalFailureMessage, now))
            .Set(item => item.UpdatedAtUtc, now),
        cancellationToken: cancellationToken);

    internal async Task<IReadOnlyList<RegistryProject>> LoadPendingCleanupAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken) => await projects.Find(ContainerRegistryFilters.PendingCleanup(now))
        .SortBy(item => item.UpdatedAtUtc)
        .Limit(limit)
        .ToListAsync(cancellationToken);

    internal async Task<IReadOnlyList<RegistryProject>> LoadAbandonedRemoteAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken) => await projects
        .Find(ContainerRegistryFilters.AbandonedRemoteProvisioning(cutoff))
        .SortBy(item => item.UpdatedAtUtc)
        .Limit(limit)
        .ToListAsync(cancellationToken);

    internal async Task<IReadOnlyList<RegistryProject>> LoadAbandonedProvisioningAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken) => await projects
        .Find(ContainerRegistryFilters.AbandonedProvisioning(cutoff))
        .SortBy(item => item.UpdatedAtUtc)
        .Limit(limit)
        .ToListAsync(cancellationToken);

    private static ClaimRegistryProvisioningOutcome Conflict(string tenantId) =>
        new TenantContainerRegistryProvisioningConflict(tenantId);

    private static RecordRemoteRegistryProvisioningOutcome ConflictUnit(string tenantId) =>
        new TenantContainerRegistryProvisioningConflict(tenantId);
}
