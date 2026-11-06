using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.Core.Multitenancy;
using Pole.Workloads;

namespace Pole.Features.GitProviders.Domain;

public enum GitProviderKind
{
    GitHub
}

public enum GitProviderAccountType
{
    User,
    Organization
}

public enum GitProviderRepositorySelection
{
    All,
    Selected
}

public sealed record GitProviderConnection(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string TechnicalName,
    GitProviderKind Provider,
    string ExternalInstallationId,
    string AccountId,
    string AccountLogin,
    GitProviderAccountType AccountType,
    GitProviderRepositorySelection RepositorySelection,
    string CreatedByUserId,
    long ReferenceFence,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc) : ITenantOwned;

public static class GitProviderConnectionFactory
{
    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, DateTime.UtcNow);

    public static GitProviderConnection Create(
        string tenantId,
        string technicalName,
        GitProviderKind provider,
        string externalInstallationId,
        string accountId,
        string accountLogin,
        GitProviderAccountType accountType,
        GitProviderRepositorySelection repositorySelection,
        string createdByUserId,
        CreateOptions options) =>
        new(
            options.Id,
            tenantId,
            ResourceTechnicalName.Require(technicalName),
            provider,
            externalInstallationId,
            accountId,
            accountLogin,
            accountType,
            repositorySelection,
            createdByUserId,
            0,
            options.CreatedAtUtc,
            options.UpdatedAtUtc);
}

public sealed record GitHubSetupAttempt(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string InitiatedByUserId,
    string TechnicalName,
    GitHubSetupState State,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc) : ITenantOwned;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(AwaitingGitHubInstallation),
    typeof(AwaitingGitHubOAuth),
    typeof(ConsumedGitHubSetup))]
public abstract record GitHubSetupState;

public sealed record AwaitingGitHubInstallation(string InstallStateHash) : GitHubSetupState;

public sealed record AwaitingGitHubOAuth(
    string OAuthStateHash,
    string ProtectedCodeVerifier,
    GitHubInstallationMetadata Installation) : GitHubSetupState;

public sealed record ConsumedGitHubSetup(DateTime ConsumedAtUtc) : GitHubSetupState;

public sealed record GitHubInstallationMetadata(
    string ExternalInstallationId,
    string AccountId,
    string AccountLogin,
    GitProviderAccountType AccountType,
    GitProviderRepositorySelection RepositorySelection);

public static class GitHubSetupAttemptFactory
{
    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10));

    public static GitHubSetupAttempt Create(
        string tenantId,
        string initiatedByUserId,
        string technicalName,
        string installStateHash,
        CreateOptions options) =>
        new(
            options.Id,
            tenantId,
            initiatedByUserId,
            ResourceTechnicalName.Require(technicalName),
            new AwaitingGitHubInstallation(installStateHash),
            options.CreatedAtUtc,
            options.ExpiresAtUtc);
}

public enum SourceBuildPlanKind
{
    Dockerfile,
    DotNet,
    Node
}

public enum SourceBuildPlanConfidence
{
    High,
    Medium,
    Low
}

public sealed record SourceBuildPlan(
    string CandidateKey,
    SourceBuildPlanKind Kind,
    string DisplayName,
    string SourceRoot,
    string ManifestPath,
    SourceBuildPlanConfidence Confidence,
    ImmutableList<string> Signals,
    ImmutableList<string> Warnings,
    ImmutableList<string> Commands,
    string? StartCommand,
    string DockerfilePreview);

public sealed record SourceRepositoryAnalysis(
    string Branch,
    string CommitSha,
    DateTime AnalyzedAtUtc,
    ImmutableList<string> DetectedFiles,
    ImmutableList<SourceBuildPlan> Candidates);

public sealed record SourceRepository(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string TechnicalName,
    string ConnectionId,
    string ExternalRepositoryId,
    string RepositoryFullName,
    string RepositoryUrl,
    bool IsPrivate,
    string Branch,
    long Revision,
    long ReferenceFence,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc) : ITenantOwned;

public static class SourceRepositoryFactory
{
    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public static CreateOptions DefaultOptions =>
        new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, DateTime.UtcNow);

    public static SourceRepository Create(
        string tenantId,
        string technicalName,
        string connectionId,
        string externalRepositoryId,
        string repositoryFullName,
        string repositoryUrl,
        bool isPrivate,
        string branch,
        CreateOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalRepositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        return new(
            options.Id,
            tenantId,
            ResourceTechnicalName.Require(technicalName),
            connectionId,
            externalRepositoryId,
            repositoryFullName,
            repositoryUrl,
            isPrivate,
            branch,
            1,
            0,
            options.CreatedAtUtc,
            options.UpdatedAtUtc);
    }
}
