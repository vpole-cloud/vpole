using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public interface IContainerRegistryProvisioningService
{
    Task<Result<RegistryProject>> EnsureTenantProjectAsync(
        string tenantId,
        string initiatedByUserId,
        CancellationToken cancellationToken = default);
}

public sealed class ContainerRegistryProvisioningService(
    IContainerRegistryDbContext dbContext,
    IContainerRegistryClient registryClient,
    IRegistryCredentialProtector credentialProtector,
    IRegistryProvisioningCleanupService cleanupService,
    TimeProvider timeProvider) : IContainerRegistryProvisioningService
{
    private const long InitialSecretVersion = 1;

    public async Task<Result<RegistryProject>> EnsureTenantProjectAsync(
        string tenantId,
        string initiatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var claim = await ClaimProvisioningAsync(tenantId, initiatedByUserId, cancellationToken);
        if (!claim.TryGetValue(out var project, out var claimFailure))
            return claimFailure;
        if (project.State is ReadyRegistryProject)
            return project;

        Result<ProvisionedRegistryProject> provisioned;
        try
        {
            provisioned = await registryClient.ProvisionProjectAsync(
                new RegistryProjectProvisioningRequest(tenantId, project.Name),
                cancellationToken);
        }
        catch
        {
            await MarkFailedAfterInterruptionAsync(project.Id);
            throw;
        }
        if (!provisioned.TryGetValue(out var remoteProject, out var provisioningFailure))
        {
            using var stateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await MarkFailedAsync(project.Id, provisioningFailure, stateTimeout.Token);
            return provisioningFailure;
        }

        var attemptId = ((ProvisioningRegistryProject)project.State).AttemptId;
        var attempt = new RegistryProvisioningAttempt(
            attemptId,
            remoteProject.ExternalProjectId,
            remoteProject.BuildCredential.ExternalRobotId,
            remoteProject.PullCredential.ExternalRobotId);
        RegistryCredential buildCredential;
        RegistryCredential pullCredential;
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var remoteState = new RemoteProvisionedRegistryProject(
                attempt.AttemptId,
                attempt.ExternalProjectId,
                attempt.BuildExternalRobotId,
                attempt.PullExternalRobotId,
                now);
            var remoteRecorded = await RecordRemoteProvisioningAsync(
                project.Id,
                remoteState,
                cancellationToken);
            if (remoteRecorded is Failure remoteStateFailure)
            {
                await CompensateFailureAsync(project.Id, attempt, remoteStateFailure);
                return remoteStateFailure;
            }

            var buildCredentialResult = await CreateCredentialAsync(
                project,
                RegistryCredentialPurpose.BuildPushPull,
                remoteProject.BuildCredential,
                now,
                cancellationToken);
            if (!buildCredentialResult.TryGetValue(out var protectedBuildCredential, out var buildCredentialFailure))
            {
                await CompensateFailureAsync(project.Id, attempt, buildCredentialFailure);
                return buildCredentialFailure;
            }
            buildCredential = protectedBuildCredential!;

            var pullCredentialResult = await CreateCredentialAsync(
                project,
                RegistryCredentialPurpose.RuntimePull,
                remoteProject.PullCredential,
                now,
                cancellationToken);
            if (!pullCredentialResult.TryGetValue(out var protectedPullCredential, out var pullCredentialFailure))
            {
                await CompensateFailureAsync(project.Id, attempt, pullCredentialFailure);
                return pullCredentialFailure;
            }
            pullCredential = protectedPullCredential!;
        }
        catch
        {
            await CompensateFailureAsync(
                project.Id,
                attempt,
                Failures.Internal(
                    "Container registry provisioning was interrupted.",
                    "Created robot credentials were queued for cleanup before the operation is retried."));
            throw;
        }

        var persistedAt = timeProvider.GetUtcNow().UtcDateTime;
        var ready = new ReadyRegistryProject(
            remoteProject.ExternalProjectId,
            buildCredential.Id,
            pullCredential.Id,
            persistedAt);
        Result<RegistryProject> persisted;
        try
        {
            persisted = await dbContext.RunInTransactionAsync<RegistryProject>(async (session, ct) =>
            {
                var update = await dbContext.RegistryProjects.UpdateOneAsync(
                    session,
                    RemoteAttemptFilter(project.Id, attempt),
                    Builders<RegistryProject>.Update
                        .Set(item => item.State, ready)
                        .Set(item => item.UpdatedAtUtc, persistedAt),
                    cancellationToken: ct);
                if (update.ModifiedCount != 1)
                    return Failures.Conflict("The registry project provisioning state changed concurrently.");

                await dbContext.RegistryCredentials.InsertManyAsync(
                    session,
                    [buildCredential, pullCredential],
                    cancellationToken: ct);
                return project with { State = ready, UpdatedAtUtc = persistedAt };
            }, cancellationToken: cancellationToken);
        }
        catch
        {
            var committed = await ResolveUnknownCommitAsync(project.Id, attempt, ready);
            if (committed is not null)
                return committed;
            throw;
        }

        if (persisted.TryGetValue(out _, out _))
            return persisted;

        if (!persisted.TryGetValue(out _, out var persistenceFailure))
            await CompensateFailureAsync(project.Id, attempt, persistenceFailure);
        return persisted;
    }

    private async Task<Result<RegistryProject>> ClaimProvisioningAsync(
        string tenantId,
        string initiatedByUserId,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.RegistryProjects
            .Find(item => item.TenantId == tenantId && item.Provider == ContainerRegistryProvider.Harbor)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing?.State is ReadyRegistryProject)
            return existing;
        if (existing?.State is ProvisioningRegistryProject)
            return Failures.Conflict("The tenant container registry is already being provisioned.");
        if (existing?.State is RemoteProvisionedRegistryProject)
            return Failures.Conflict("The tenant container registry has an incomplete provisioning attempt awaiting reconciliation.");
        if (existing?.State is CleanupRequiredRegistryProjectProvisioning)
            return Failures.Conflict("The tenant container registry is waiting for robot credential cleanup.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (existing?.State is FailedRegistryProjectProvisioning)
        {
            var provisioning = new ProvisioningRegistryProject(Guid.NewGuid().ToString("N"), now);
            var claimed = await dbContext.RegistryProjects.FindOneAndUpdateAsync(
                Builders<RegistryProject>.Filter.And(
                    Builders<RegistryProject>.Filter.Eq(item => item.Id, existing.Id),
                    Builders<RegistryProject>.Filter.OfType<RegistryProjectState, FailedRegistryProjectProvisioning>(
                        item => item.State)),
                Builders<RegistryProject>.Update
                    .Set(item => item.State, provisioning)
                    .Set(item => item.UpdatedAtUtc, now),
                new FindOneAndUpdateOptions<RegistryProject> { ReturnDocument = ReturnDocument.After },
                cancellationToken);
            return claimed is null
                ? Failures.Conflict("The tenant container registry provisioning state changed concurrently.")
                : claimed;
        }

        var project = RegistryProjectFactory.Create(
            tenantId,
            ContainerRegistryProvider.Harbor,
            RegistryProjectName.ForTenant(tenantId),
            initiatedByUserId,
            new RegistryProjectFactory.CreateOptions(MongoDB.Bson.ObjectId.GenerateNewId().ToString(), now));
        return await dbContext.RegistryProjects.InsertOneAsResultAsync(
            project,
            () => Failures.Conflict(
                "The tenant container registry is already being provisioned."),
            cancellationToken);
    }

    private async ValueTask<Result<RegistryCredential>> CreateCredentialAsync(
        RegistryProject project,
        RegistryCredentialPurpose purpose,
        RegistryRobotCredential credential,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var credentialId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var encryptedSecret = await credentialProtector.ProtectAsync(
            project.TenantId,
            credentialId,
            InitialSecretVersion,
            credential.Secret,
            cancellationToken);
        if (!encryptedSecret.TryGetValue(out var encryptedValue, out var failure))
            return failure;

        return RegistryCredentialFactory.Create(
            project.TenantId,
            project.Id,
            purpose,
            credential.ExternalRobotId,
            credential.Username,
            InitialSecretVersion,
            encryptedValue,
            credential.ExpiresAtUtc,
            new RegistryCredentialFactory.CreateOptions(credentialId, now));
    }

    private async Task MarkFailedAsync(
        string projectId,
        Failure failure,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var code = failure.GetType().Name;
        await dbContext.RegistryProjects.UpdateOneAsync(
            Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
                Builders<RegistryProject>.Filter.OfType<RegistryProjectState, ProvisioningRegistryProject>(
                    item => item.State)),
            Builders<RegistryProject>.Update
                .Set(item => item.State, new FailedRegistryProjectProvisioning(code, failure.Message, now))
                .Set(item => item.UpdatedAtUtc, now),
            cancellationToken: cancellationToken);
    }

    private async Task MarkFailedAfterInterruptionAsync(string projectId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await MarkFailedAsync(
                projectId,
                Failures.Internal(
                    "Container registry provisioning was interrupted.",
                    "The provider request did not return a complete provisioning result."),
                timeout.Token);
        }
        catch (Exception)
        {
            // Preserve the provider exception. A remaining Provisioning state is visible to operators.
        }
    }

    private async Task<Result> RecordRemoteProvisioningAsync(
        string projectId,
        RemoteProvisionedRegistryProject remoteState,
        CancellationToken cancellationToken)
    {
        var update = await dbContext.RegistryProjects.UpdateOneAsync(
            Builders<RegistryProject>.Filter.And(
                Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
                Builders<RegistryProject>.Filter.OfType<RegistryProjectState, ProvisioningRegistryProject>(
                    item => item.State),
                Builders<RegistryProject>.Filter.Eq(
                    item => ((ProvisioningRegistryProject)item.State).AttemptId,
                    remoteState.AttemptId)),
            Builders<RegistryProject>.Update
                .Set(item => item.State, remoteState)
                .Set(item => item.UpdatedAtUtc, remoteState.ProvisionedAtUtc),
            cancellationToken: cancellationToken);
        return update.ModifiedCount == 1
            ? Result.Success()
            : Failures.Conflict("The registry project provisioning state changed concurrently.");
    }

    private async Task CompensateFailureAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        Failure failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var cleanup = await cleanupService.ClaimCleanupAsync(
                projectId,
                attempt,
                failure,
                timeout.Token);
            cleanup ??= await FindMatchingCleanupAsync(projectId, attempt, timeout.Token);
            if (cleanup is not null)
                await cleanupService.CleanupAsync(projectId, cleanup, timeout.Token);
        }
        catch (Exception)
        {
            // Never replace the original provisioning failure. Cleanup is allowed only after its
            // durable state transition succeeds; a Mongo outage therefore leaves Harbor untouched.
        }
    }

    private async Task<RegistryProject?> ResolveUnknownCommitAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        ReadyRegistryProject expectedReady)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var cleanup = await cleanupService.ClaimCleanupAsync(
                projectId,
                attempt,
                Failures.Internal(
                    "Container registry provisioning persistence was interrupted.",
                    "The persisted state was reconciled before any remote cleanup."),
                timeout.Token);
            cleanup ??= await FindMatchingCleanupAsync(projectId, attempt, timeout.Token);
            if (cleanup is not null)
            {
                await cleanupService.CleanupAsync(projectId, cleanup, timeout.Token);
                return null;
            }

            var current = await dbContext.RegistryProjects
                .Find(item => item.Id == projectId)
                .FirstOrDefaultAsync(timeout.Token);
            return current?.State is ReadyRegistryProject ready &&
                   ready.ExternalProjectId == expectedReady.ExternalProjectId &&
                   ready.BuildCredentialId == expectedReady.BuildCredentialId &&
                   ready.PullCredentialId == expectedReady.PullCredentialId
                ? current
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<CleanupRequiredRegistryProjectProvisioning?> FindMatchingCleanupAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        CancellationToken cancellationToken)
    {
        var project = await dbContext.RegistryProjects
            .Find(item => item.Id == projectId)
            .FirstOrDefaultAsync(cancellationToken);
        return project?.State is CleanupRequiredRegistryProjectProvisioning cleanup &&
               cleanup.AttemptId == attempt.AttemptId &&
               cleanup.ExternalProjectId == attempt.ExternalProjectId &&
               cleanup.BuildExternalRobotId == attempt.BuildExternalRobotId &&
               cleanup.PullExternalRobotId == attempt.PullExternalRobotId
            ? cleanup
            : null;
    }

    private static FilterDefinition<RegistryProject> RemoteAttemptFilter(
        string projectId,
        RegistryProvisioningAttempt attempt) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
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
}
