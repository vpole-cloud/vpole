// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Projects;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Provisioning;

public union EnsureTenantContainerRegistryProjectOutcome(
    RegistryProject,
    TenantContainerRegistryProvisioningConflict,
    ContainerRegistryProviderError,
    RegistryCredentialProtectionFailed);

public delegate Task<EnsureTenantContainerRegistryProjectOutcome> EnsureTenantContainerRegistryProject(
    string tenantId,
    string initiatedByUserId,
    CancellationToken cancellationToken = default);

internal union CreateRegistryCredentialOutcome(RegistryCredential, RegistryCredentialProtectionFailed);

public sealed class ContainerRegistryProvisioningService(
    RegistryProvisioningPersistence persistence,
    IContainerRegistryClient registryClient,
    ProtectRegistryCredentialSecret protectSecret,
    RegistryProvisioningCleanupService cleanupService,
    TimeProvider timeProvider
)
{
    private const long InitialSecretVersion = 1;

    public async Task<EnsureTenantContainerRegistryProjectOutcome> InvokeAsync(
        string tenantId,
        string initiatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var claim = await persistence.ClaimProvisioningAsync(tenantId, initiatedByUserId, cancellationToken);
        if (claim.Value is TenantContainerRegistryProvisioningConflict claimError)
            return claimError;
        var project = (RegistryProject)claim.Value;
        if (project.State is ReadyRegistryProject)
            return project;

        var provisioning = (ProvisioningRegistryProject)project.State;
        var names = RegistryRobotNames.ForAttempt(provisioning.AttemptId);
        var nameOnlyAttempt = new RegistryProvisioningAttempt(
            provisioning.AttemptId,
            project.Name,
            project.Name,
            names.Build,
            names.Pull,
            null,
            null,
            provisioning.StartedAtUtc + RegistryProvisioningCleanupPolicy.NameVerificationHorizon);

        ProvisionRegistryProjectOutcome provisioned;
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

        if (provisioned.Value is ContainerRegistryProviderError providerError)
        {
            await CompensateFailureAsync(project.Id, nameOnlyAttempt, ProviderFailure(providerError));
            return providerError;
        }

        var remoteProject = (ProvisionedRegistryProject)provisioned.Value;
        var attempt = new RegistryProvisioningAttempt(
            provisioning.AttemptId,
            project.Name,
            remoteProject.ExternalProjectId,
            names.Build,
            names.Pull,
            remoteProject.BuildCredential.ExternalRobotId,
            remoteProject.PullCredential.ExternalRobotId,
            null);
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
            if (remoteRecorded.Value is TenantContainerRegistryProvisioningConflict remoteConflict)
            {
                await CompensateFailureAsync(project.Id, attempt, ConcurrentFailure());
                return remoteConflict;
            }

            var build = await CreateCredentialAsync(
                project,
                RegistryCredentialPurpose.BuildPushPull,
                remoteProject.BuildCredential,
                now,
                cancellationToken);
            if (build.Value is RegistryCredentialProtectionFailed buildFailure)
            {
                await CompensateFailureAsync(project.Id, attempt, CredentialFailure());
                return buildFailure;
            }

            var protectedBuild = (RegistryCredential)build.Value;
            var pull = await CreateCredentialAsync(
                project,
                RegistryCredentialPurpose.RuntimePull,
                remoteProject.PullCredential,
                now,
                cancellationToken);
            if (pull.Value is RegistryCredentialProtectionFailed pullFailure)
            {
                await CompensateFailureAsync(project.Id, attempt, CredentialFailure());
                return pullFailure;
            }

            var protectedPull = (RegistryCredential)pull.Value;

            var persistedAt = timeProvider.GetUtcNow().UtcDateTime;
            var ready = new ReadyRegistryProject(
                remoteProject.ExternalProjectId,
                protectedBuild.Id,
                protectedPull.Id,
                persistedAt);
            PersistReadyRegistryProjectOutcome persisted;
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
                    return committed;
                throw;
            }

            if (persisted.Value is TenantContainerRegistryProvisioningConflict persistError)
            {
                await CompensateFailureAsync(project.Id, attempt, ConcurrentFailure());
                return persistError;
            }

            return (RegistryProject)persisted.Value;
        }
        catch
        {
            await CompensateFailureAsync(project.Id, attempt, InterruptedFailure());
            throw;
        }
    }

    private async ValueTask<CreateRegistryCredentialOutcome> CreateCredentialAsync(
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
            EncryptedValue success => RegistryCredentialFactory.Create(
                project.TenantId,
                project.Id,
                purpose,
                credential.ExternalRobotId,
                credential.Username,
                InitialSecretVersion,
                success,
                credential.ExpiresAtUtc,
                new RegistryCredentialFactory.CreateOptions(credentialId, now)),
            RegistryCredentialProtectionFailed error => error,
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
                RegistryProvisioningFailure.Interrupted("Container registry provisioning persistence was interrupted."),
                timeout.Token) ?? await persistence.FindMatchingCleanupAsync(projectId, attempt, timeout.Token);
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

    private static RegistryProvisioningFailure ProviderFailure(ContainerRegistryProviderError error) => error switch
    {
        ContainerRegistryProviderNotConfigured => new RegistryProvisioningFailure(
            "container-registries.provider.not-configured",
            "Harbor is not configured."),
        ContainerRegistryProviderUnavailable => new RegistryProvisioningFailure(
            "container-registries.provider.unavailable",
            "Harbor is unavailable."),
    };

    private static RegistryProvisioningFailure ConcurrentFailure() => new(
        "container-registries.provisioning.conflict",
        "The registry project provisioning state changed concurrently.");

    private static RegistryProvisioningFailure CredentialFailure() => new(
        "container-registries.credential.protection-failed",
        "The registry credential could not be protected.");

    private static RegistryProvisioningFailure InterruptedFailure() =>
        RegistryProvisioningFailure.Interrupted("Container registry provisioning was interrupted.");
}
