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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var buildCredential = CreateCredential(
            project,
            RegistryCredentialPurpose.BuildPushPull,
            remoteProject.BuildCredential,
            now);
        var pullCredential = CreateCredential(
            project,
            RegistryCredentialPurpose.RuntimePull,
            remoteProject.PullCredential,
            now);
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

        await registryClient.DeleteProjectAsync(remoteProject.ExternalProjectId, cancellationToken);
        if (!persisted.TryGetValue(out _, out var persistenceFailure))
            await MarkFailedAsync(project.Id, persistenceFailure, cancellationToken);
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
        try
        {
            await dbContext.RegistryProjects.InsertOneAsync(project, cancellationToken: cancellationToken);
            return project;
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict("The tenant container registry is already being provisioned.");
        }
    }

    private RegistryCredential CreateCredential(
        RegistryProject project,
        RegistryCredentialPurpose purpose,
        RegistryRobotCredential credential,
        DateTime now) =>
        RegistryCredentialFactory.Create(
            project.TenantId,
            project.Id,
            purpose,
            credential.ExternalRobotId,
            credential.Username,
            credentialProtector.Protect(credential.Secret),
            credential.ExpiresAtUtc,
            new RegistryCredentialFactory.CreateOptions(
                MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                now));

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
}
