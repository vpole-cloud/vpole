using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Mongo;

internal static class ContainerRegistryFilters
{
    internal static FilterDefinition<RegistryProject> ProjectById(string projectId) =>
        Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId);

    internal static FilterDefinition<RegistryProject> HarborProjectByTenant(string tenantId) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.ForTenant(tenantId),
            Builders<RegistryProject>.Filter.Eq(item => item.Provider, ContainerRegistryProvider.Harbor));

    internal static FilterDefinition<RegistryProject> HarborProjectByTenantAndId(string tenantId, string projectId) =>
        Builders<RegistryProject>.Filter.And(
            HarborProjectByTenant(tenantId),
            Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId));

    internal static FilterDefinition<RegistryCredential> CredentialByTenantProjectAndPurpose(
        string tenantId,
        string projectId,
        RegistryCredentialPurpose purpose) => Builders<RegistryCredential>.Filter.And(
        Builders<RegistryCredential>.Filter.ForTenant(tenantId),
        Builders<RegistryCredential>.Filter.Eq(item => item.RegistryProjectId, projectId),
        Builders<RegistryCredential>.Filter.Eq(item => item.Purpose, purpose));

    internal static FilterDefinition<RegistryCredential> CredentialForConcurrentRewrap(
        string credentialId,
        EncryptedValue encryptedSecret) => Builders<RegistryCredential>.Filter.And(
        Builders<RegistryCredential>.Filter.Eq(item => item.Id, credentialId),
        Builders<RegistryCredential>.Filter.Eq(item => item.EncryptedSecret, encryptedSecret));

    internal static FilterDefinition<RegistryCredential> CredentialsAfter(string? credentialId) => credentialId is null
        ? Builders<RegistryCredential>.Filter.Empty
        : Builders<RegistryCredential>.Filter.Gt(item => item.Id, credentialId);

    internal static FilterDefinition<RegistryProject> ProvisioningAttempt(string projectId, string attemptId) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
            Builders<RegistryProject>.Filter.OfType<RegistryProjectState, ProvisioningRegistryProject>(item =>
                item.State),
            Builders<RegistryProject>.Filter.Eq(
                item => ((ProvisioningRegistryProject)item.State).AttemptId,
                attemptId));

    internal static FilterDefinition<RegistryProject>
        RemoteProvisioningAttempt(string projectId, RegistryProvisioningAttempt attempt) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
            Builders<RegistryProject>.Filter.OfType<RegistryProjectState, RemoteProvisionedRegistryProject>(item =>
                item.State),
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

    internal static FilterDefinition<RegistryProject> FailedProvisioning(string projectId) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
            Builders<RegistryProject>.Filter.OfType<RegistryProjectState, FailedRegistryProjectProvisioning>(item =>
                item.State));

    internal static FilterDefinition<RegistryProject>
        CleanupLeaseCandidate(
            string projectId,
            CleanupRequiredRegistryProjectProvisioning cleanup,
            DateTime now) => Builders<RegistryProject>.Filter.And(
        Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
        Builders<RegistryProject>.Filter
            .OfType<RegistryProjectState, CleanupRequiredRegistryProjectProvisioning>(item => item.State),
        Builders<RegistryProject>.Filter.Eq(
            item => ((CleanupRequiredRegistryProjectProvisioning)item.State).AttemptId,
            cleanup.AttemptId),
        Builders<RegistryProject>.Filter.Eq(
            item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupRequiredAtUtc,
            cleanup.CleanupRequiredAtUtc),
        Builders<RegistryProject>.Filter.Lte(
            item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupNotBeforeUtc,
            now),
        Builders<RegistryProject>.Filter.Or(
            Builders<RegistryProject>.Filter.Eq(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseToken,
                null),
            Builders<RegistryProject>.Filter.Lte(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseExpiresAtUtc,
                now)));

    internal static FilterDefinition<RegistryProject>
        CleanupLease(string projectId, CleanupRequiredRegistryProjectProvisioning cleanup) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.Eq(item => item.Id, projectId),
            Builders<RegistryProject>.Filter
                .OfType<RegistryProjectState, CleanupRequiredRegistryProjectProvisioning>(item => item.State),
            Builders<RegistryProject>.Filter.Eq(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupRequiredAtUtc,
                cleanup.CleanupRequiredAtUtc),
            Builders<RegistryProject>.Filter.Eq(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).AttemptId,
                cleanup.AttemptId),
            Builders<RegistryProject>.Filter.Eq(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupLeaseToken,
                cleanup.CleanupLeaseToken));

    internal static FilterDefinition<RegistryProject> CleanupClaimExpectedState(
        string projectId,
        RegistryProvisioningAttempt attempt) => attempt.NameVerificationNotBeforeUtc is null
        ? Builders<RegistryProject>.Filter.Or(
            ProvisioningAttempt(projectId, attempt.AttemptId),
            RemoteProvisioningAttempt(projectId, attempt))
        : ProvisioningAttempt(projectId, attempt.AttemptId);

    internal static FilterDefinition<RegistryProject> PendingCleanup(DateTime now) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter
                .OfType<RegistryProjectState, CleanupRequiredRegistryProjectProvisioning>(item => item.State),
            Builders<RegistryProject>.Filter.Lte(
                item => ((CleanupRequiredRegistryProjectProvisioning)item.State).CleanupNotBeforeUtc,
                now));

    internal static FilterDefinition<RegistryProject> AbandonedRemoteProvisioning(DateTime cutoff) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.OfType<RegistryProjectState, RemoteProvisionedRegistryProject>(item =>
                item.State),
            Builders<RegistryProject>.Filter.Lte(item => item.UpdatedAtUtc, cutoff));

    internal static FilterDefinition<RegistryProject> AbandonedProvisioning(DateTime cutoff) =>
        Builders<RegistryProject>.Filter.And(
            Builders<RegistryProject>.Filter.OfType<RegistryProjectState, ProvisioningRegistryProject>(item =>
                item.State),
            Builders<RegistryProject>.Filter.Lte(item => item.UpdatedAtUtc, cutoff));
}
