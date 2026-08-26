using MongoDB.Bson;
using Pole.Features.ContainerRegistries.Application.Persistence;
using Pole.Features.ContainerRegistries.Application.Persistence.Projects;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public delegate Task<Result<RegistryProject, EnsureTenantContainerRegistryProjectError>>
    EnsureTenantContainerRegistryProject(
        string tenantId,
        string initiatedByUserId,
        CancellationToken cancellationToken = default);

public sealed class ContainerRegistryProvisioningService(
    RegistryProvisioningPersistence persistence,
    IContainerRegistryClient registryClient,
    ProtectRegistryCredentialSecret protectSecret,
    RegistryProvisioningCleanupService cleanupService,
    TimeProvider timeProvider)
{
    private const long InitialSecretVersion = 1;

    public async Task<Result<RegistryProject, EnsureTenantContainerRegistryProjectError>> InvokeAsync(
        string tenantId,
        string initiatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var claim = await persistence.ClaimProvisioningAsync(tenantId, initiatedByUserId, cancellationToken);
        var claimed = TypedResultExtensions.TryGetValue(claim);
        if (!claimed.Succeeded)
            return new Err<EnsureTenantContainerRegistryProjectError>(new(claimed.Error!));
        var project = claimed.Value!;
        if (project.State is ReadyRegistryProject)
            return new Ok<RegistryProject>(project);

        var provisioning = (ProvisioningRegistryProject)project.State;
        var names = RegistryRobotNames.ForAttempt(provisioning.AttemptId);
        var nameOnlyAttempt = new RegistryProvisioningAttempt(
            provisioning.AttemptId, project.Name, project.Name, names.Build, names.Pull,
            null, null,
            provisioning.StartedAtUtc + RegistryProvisioningCleanupPolicy.NameVerificationHorizon);

        Result<ProvisionedRegistryProject, ContainerRegistryProviderError> provisioned;
        try
        {
            provisioned = await registryClient.ProvisionProjectAsync(
                new RegistryProjectProvisioningRequest(tenantId, project.Name, provisioning.AttemptId),
                cancellationToken);
        }
        catch
        {
            await CompensateFailureAsync(project.Id, nameOnlyAttempt, InterruptedFailure());
            throw;
        }
        var provisionedProject = TypedResultExtensions.TryGetValue(provisioned);
        if (!provisionedProject.Succeeded)
        {
            await CompensateFailureAsync(project.Id, nameOnlyAttempt, ProviderFailure(provisionedProject.Error!));
            return new Err<EnsureTenantContainerRegistryProjectError>(new(provisionedProject.Error!));
        }
        var remoteProject = provisionedProject.Value!;
        var attempt = new RegistryProvisioningAttempt(
            provisioning.AttemptId, project.Name, remoteProject.ExternalProjectId, names.Build, names.Pull,
            remoteProject.BuildCredential.ExternalRobotId, remoteProject.PullCredential.ExternalRobotId, null);
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var remoteRecorded = await persistence.RecordRemoteProvisioningAsync(
                project.Id,
                new RemoteProvisionedRegistryProject(
                    attempt.AttemptId,
                    attempt.ExternalProjectId,
                    remoteProject.BuildCredential.ExternalRobotId,
                    remoteProject.PullCredential.ExternalRobotId,
                    now),
                tenantId,
                cancellationToken);
            if (remoteRecorded is Err<TenantContainerRegistryProvisioningConflict> remoteConflict)
            {
                await CompensateFailureAsync(project.Id, attempt, ConcurrentFailure());
                return new Err<EnsureTenantContainerRegistryProjectError>(new(remoteConflict.Value));
            }

            var build = await CreateCredentialAsync(
                project, RegistryCredentialPurpose.BuildPushPull, remoteProject.BuildCredential, now, cancellationToken);
            var protectedBuildResult = TypedResultExtensions.TryGetValue(build);
            if (!protectedBuildResult.Succeeded)
            {
                await CompensateFailureAsync(project.Id, attempt, CredentialFailure());
                return new Err<EnsureTenantContainerRegistryProjectError>(new(protectedBuildResult.Error!));
            }
            var protectedBuild = protectedBuildResult.Value!;
            var pull = await CreateCredentialAsync(
                project, RegistryCredentialPurpose.RuntimePull, remoteProject.PullCredential, now, cancellationToken);
            var protectedPullResult = TypedResultExtensions.TryGetValue(pull);
            if (!protectedPullResult.Succeeded)
            {
                await CompensateFailureAsync(project.Id, attempt, CredentialFailure());
                return new Err<EnsureTenantContainerRegistryProjectError>(new(protectedPullResult.Error!));
            }
            var protectedPull = protectedPullResult.Value!;

            var persistedAt = timeProvider.GetUtcNow().UtcDateTime;
            var ready = new ReadyRegistryProject(
                remoteProject.ExternalProjectId,
                protectedBuild.Id,
                protectedPull.Id,
                persistedAt);
            Result<RegistryProject, TenantContainerRegistryProvisioningConflict> persisted;
            try
            {
                persisted = await persistence.PersistReadyAsync(
                    project,
                    attempt,
                    ready,
                    protectedBuild,
                    protectedPull,
                    cancellationToken);
            }
            catch
            {
                var committed = await ResolveUnknownCommitAsync(project.Id, attempt, ready);
                if (committed is not null)
                    return new Ok<RegistryProject>(committed);
                throw;
            }
            var persistedProject = TypedResultExtensions.TryGetValue(persisted);
            if (!persistedProject.Succeeded)
            {
                await CompensateFailureAsync(project.Id, attempt, ConcurrentFailure());
                return new Err<EnsureTenantContainerRegistryProjectError>(new(persistedProject.Error!));
            }
            return new Ok<RegistryProject>(persistedProject.Value!);
        }
        catch
        {
            await CompensateFailureAsync(project.Id, attempt, InterruptedFailure());
            throw;
        }
    }

    private async ValueTask<Result<RegistryCredential, RegistryCredentialProtectionFailed>> CreateCredentialAsync(
        RegistryProject project,
        RegistryCredentialPurpose purpose,
        RegistryRobotCredential credential,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var credentialId = ObjectId.GenerateNewId().ToString();
        var encrypted = await protectSecret(
            project.TenantId,
            credentialId,
            InitialSecretVersion,
            credential.Secret,
            cancellationToken);
        return encrypted switch
        {
            Ok<Pole.Encryption.EncryptedValue> success => new Ok<RegistryCredential>(
                RegistryCredentialFactory.Create(
                    project.TenantId,
                    project.Id,
                    purpose,
                    credential.ExternalRobotId,
                    credential.Username,
                    InitialSecretVersion,
                    success.Value,
                    credential.ExpiresAtUtc,
                    new RegistryCredentialFactory.CreateOptions(credentialId, now))),
            Err<RegistryCredentialProtectionFailed> error =>
                new Err<RegistryCredentialProtectionFailed>(error.Value)
        };
    }

    private async Task CompensateFailureAsync(
        string projectId,
        RegistryProvisioningAttempt attempt,
        RegistryProvisioningFailure failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var cleanup = await cleanupService.ClaimCleanupAsync(projectId, attempt, failure, timeout.Token) ??
                          await persistence.FindMatchingCleanupAsync(projectId, attempt, timeout.Token);
            if (cleanup is not null && cleanup.CleanupNotBeforeUtc <= timeProvider.GetUtcNow().UtcDateTime)
                await cleanupService.CleanupAsync(projectId, cleanup, timeout.Token);
        }
        catch (Exception)
        {
            // Never replace the original failure. Cleanup is attempted only after its durable transition.
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
                              RegistryProvisioningFailure.Interrupted(
                                  "Container registry provisioning persistence was interrupted."),
                              timeout.Token) ??
                          await persistence.FindMatchingCleanupAsync(projectId, attempt, timeout.Token);
            if (cleanup is not null)
            {
                await cleanupService.CleanupAsync(projectId, cleanup, timeout.Token);
                return null;
            }

            var current = await persistence.LoadByIdAsync(projectId, timeout.Token);
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

    private static RegistryProvisioningFailure ProviderFailure(ContainerRegistryProviderError error) =>
        error switch
        {
            ContainerRegistryProviderNotConfigured => new("container-registries.provider.not-configured", "Harbor is not configured."),
            ContainerRegistryProviderUnavailable => new("container-registries.provider.unavailable", "Harbor is unavailable.")
        };

    private static RegistryProvisioningFailure ConcurrentFailure() =>
        new("container-registries.provisioning.conflict", "The registry project provisioning state changed concurrently.");

    private static RegistryProvisioningFailure CredentialFailure() =>
        new("container-registries.credential.protection-failed", "The registry credential could not be protected.");

    private static RegistryProvisioningFailure InterruptedFailure() =>
        RegistryProvisioningFailure.Interrupted("Container registry provisioning was interrupted.");
}
