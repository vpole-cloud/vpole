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

        var provisioned = await registryClient.ProvisionProjectAsync(
            new RegistryProjectProvisioningRequest(tenantId, project.Name),
            cancellationToken);
        if (!provisioned.TryGetValue(out var remoteProject, out var provisioningFailure))
        {
            await MarkFailedAsync(project.Id, provisioningFailure, cancellationToken);
            return provisioningFailure;
        }

        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var buildCredentialResult = await CreateCredentialAsync(
                project,
                RegistryCredentialPurpose.BuildPushPull,
                remoteProject.BuildCredential,
                now,
                cancellationToken);
            if (!buildCredentialResult.TryGetValue(out var buildCredential, out var buildCredentialFailure))
            {
                await CompensateAsync(project.Id, remoteProject.ExternalProjectId, buildCredentialFailure);
                return buildCredentialFailure;
            }

            var pullCredentialResult = await CreateCredentialAsync(
                project,
                RegistryCredentialPurpose.RuntimePull,
                remoteProject.PullCredential,
                now,
                cancellationToken);
            if (!pullCredentialResult.TryGetValue(out var pullCredential, out var pullCredentialFailure))
            {
                await CompensateAsync(project.Id, remoteProject.ExternalProjectId, pullCredentialFailure);
                return pullCredentialFailure;
            }
            var ready = new ReadyRegistryProject(
                remoteProject.ExternalProjectId,
                buildCredential.Id,
                pullCredential.Id,
                now);

            var persisted = await dbContext.RunInTransactionAsync<RegistryProject>(async (session, ct) =>
            {
                var update = await dbContext.RegistryProjects.UpdateOneAsync(
                    session,
                    Builders<RegistryProject>.Filter.And(
                        Builders<RegistryProject>.Filter.Eq(item => item.Id, project.Id),
                        Builders<RegistryProject>.Filter.OfType<RegistryProjectState, ProvisioningRegistryProject>(
                            item => item.State)),
                    Builders<RegistryProject>.Update
                        .Set(item => item.State, ready)
                        .Set(item => item.UpdatedAtUtc, now),
                    cancellationToken: ct);
                if (update.ModifiedCount != 1)
                    return Failures.Conflict("The registry project provisioning state changed concurrently.");

                await dbContext.RegistryCredentials.InsertManyAsync(
                    session,
                    [buildCredential, pullCredential],
                    cancellationToken: ct);
                return project with { State = ready, UpdatedAtUtc = now };
            }, cancellationToken: cancellationToken);

            if (persisted.TryGetValue(out _, out _))
                return persisted;

            if (!persisted.TryGetValue(out _, out var persistenceFailure))
                await CompensateAsync(project.Id, remoteProject.ExternalProjectId, persistenceFailure);
            return persisted;
        }
        catch
        {
            await CompensateAsync(
                project.Id,
                remoteProject.ExternalProjectId,
                Failures.Internal(
                    "Container registry provisioning was interrupted.",
                    "Remote resource cleanup was requested before the operation was retried."));
            throw;
        }
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (existing?.State is FailedRegistryProjectProvisioning)
        {
            var provisioning = new ProvisioningRegistryProject(now);
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

    private async Task CompensateAsync(
        string projectId,
        string externalProjectId,
        Failure failure)
    {
        using var deletionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await registryClient.DeleteProjectAsync(externalProjectId, deletionTimeout.Token);
        }
        catch (Exception)
        {
            // Best effort: never replace the original provisioning result or exception with cleanup failure.
        }

        using var stateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await MarkFailedAsync(projectId, failure, stateTimeout.Token);
        }
        catch (Exception)
        {
            // The persisted Provisioning state remains an explicit signal for operator recovery.
        }
    }
}
