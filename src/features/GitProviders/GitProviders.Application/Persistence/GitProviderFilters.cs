using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence;

internal static class GitProviderFilters
{
    internal static FilterDefinition<GitProviderConnection> GitHubConnectionByTenantAndId(
        string tenantId, string connectionId) =>
        ConnectionByTenantAndId(tenantId, connectionId) &
        Builders<GitProviderConnection>.Filter.Eq(item => item.Provider, GitProviderKind.GitHub);

    internal static FilterDefinition<GitHubDeliveryReceipt> GitHubDeliveryById(string id) =>
        Builders<GitHubDeliveryReceipt>.Filter.Eq(item => item.Id, id);

    internal static FilterDefinition<GitProviderConnection> ConnectionsByTenant(string tenantId) =>
        Builders<GitProviderConnection>.Filter.ForTenant(tenantId);

    internal static FilterDefinition<GitProviderConnection> ConnectionByTenantAndId(
        string tenantId,
        string connectionId) =>
        Builders<GitProviderConnection>.Filter.And(
            Builders<GitProviderConnection>.Filter.ForTenant(tenantId),
            Builders<GitProviderConnection>.Filter.Eq(item => item.Id, connectionId));

    internal static FilterDefinition<GitProviderConnection> ConnectionAtFence(
        string tenantId,
        string connectionId,
        long referenceFence) =>
        ConnectionByTenantAndId(tenantId, connectionId) &
        Builders<GitProviderConnection>.Filter.Eq(item => item.ReferenceFence, referenceFence);

    internal static FilterDefinition<GitProviderConnection> ConnectionByTenantAndName(
        string tenantId,
        string technicalName) =>
        Builders<GitProviderConnection>.Filter.And(
            ConnectionsByTenant(tenantId),
            Builders<GitProviderConnection>.Filter.Eq(item => item.TechnicalName, technicalName));

    internal static FilterDefinition<GitProviderConnection> ConnectionByInstallation(
        string installationId) =>
        Builders<GitProviderConnection>.Filter.And(
            Builders<GitProviderConnection>.Filter.Eq(item => item.Provider, GitProviderKind.GitHub),
            Builders<GitProviderConnection>.Filter.Eq(
                item => item.ExternalInstallationId,
                installationId));

    internal static FilterDefinition<GitProviderConnection> ConnectionById(string connectionId) =>
        Builders<GitProviderConnection>.Filter.Eq(item => item.Id, connectionId);

    internal static FilterDefinition<SourceRepository> SourceRepositoryByTenantAndId(
        string tenantId,
        string sourceRepositoryId) =>
        Builders<SourceRepository>.Filter.And(
            Builders<SourceRepository>.Filter.ForTenant(tenantId),
            Builders<SourceRepository>.Filter.Eq(item => item.Id, sourceRepositoryId));

    internal static FilterDefinition<SourceRepository> SourceRepositoryByTenantAndName(
        string tenantId,
        string technicalName) =>
        SourceRepositoriesByTenant(tenantId) &
        Builders<SourceRepository>.Filter.Eq(item => item.TechnicalName, technicalName);

    internal static FilterDefinition<SourceRepository> SourceRepositoryAtRevision(
        string tenantId,
        string sourceRepositoryId,
        long revision) =>
        SourceRepositoryByTenantAndId(tenantId, sourceRepositoryId) &
        Builders<SourceRepository>.Filter.Eq(item => item.Revision, revision);

    internal static FilterDefinition<SourceRepository> SourceRepositoriesByTenant(string tenantId) =>
        Builders<SourceRepository>.Filter.ForTenant(tenantId);

    internal static FilterDefinition<SourceRepository> SourceRepositoriesByTenantAndConnection(
        string tenantId,
        string connectionId) =>
        Builders<SourceRepository>.Filter.And(
            Builders<SourceRepository>.Filter.ForTenant(tenantId),
            Builders<SourceRepository>.Filter.Eq(item => item.ConnectionId, connectionId));

    internal static FilterDefinition<GitHubSetupAttempt> AwaitingGitHubInstallation(
        string stateHash,
        DateTime now) =>
        Builders<GitHubSetupAttempt>.Filter.And(
            Builders<GitHubSetupAttempt>.Filter.Eq(
                item => ((AwaitingGitHubInstallation)item.State).InstallStateHash,
                stateHash),
            Builders<GitHubSetupAttempt>.Filter.OfType<GitHubSetupState, AwaitingGitHubInstallation>(
                item => item.State),
            Builders<GitHubSetupAttempt>.Filter.Gt(item => item.ExpiresAtUtc, now));

    internal static FilterDefinition<GitHubSetupAttempt> AwaitingGitHubInstallationById(
        string attemptId) =>
        Builders<GitHubSetupAttempt>.Filter.And(
            Builders<GitHubSetupAttempt>.Filter.Eq(item => item.Id, attemptId),
            Builders<GitHubSetupAttempt>.Filter.OfType<GitHubSetupState, AwaitingGitHubInstallation>(
                item => item.State));

    internal static FilterDefinition<GitHubSetupAttempt> AwaitingGitHubOAuth(
        string stateHash,
        DateTime now) =>
        Builders<GitHubSetupAttempt>.Filter.And(
            Builders<GitHubSetupAttempt>.Filter.Eq(
                item => ((AwaitingGitHubOAuth)item.State).OAuthStateHash,
                stateHash),
            Builders<GitHubSetupAttempt>.Filter.OfType<GitHubSetupState, AwaitingGitHubOAuth>(
                item => item.State),
            Builders<GitHubSetupAttempt>.Filter.Gt(item => item.ExpiresAtUtc, now));
}
