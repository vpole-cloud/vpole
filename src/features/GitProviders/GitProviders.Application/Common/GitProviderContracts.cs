using System.Collections.Immutable;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Common;

public sealed record GitProviderConnectionDto(
    string Id,
    string Provider,
    string AccountLogin,
    string AccountType,
    string RepositorySelection,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record BeginGitHubInstallationResponse(
    string InstallUrl,
    DateTime ExpiresAtUtc);

public sealed record ProviderRepositoryDto(
    string Id,
    string Name,
    string FullName,
    string HtmlUrl,
    bool IsPrivate,
    string DefaultBranch,
    DateTime? UpdatedAtUtc);

public sealed record ProviderBranchDto(
    string Name,
    string CommitSha,
    bool IsProtected);

public sealed record ProviderPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    bool HasMore);

public sealed record AnalyzeRepositoryRequestBody(string Branch);

public sealed record LinkSourceRepositoryRequestBody(
    string Branch,
    string CandidateKey);

public sealed record RepositoryAnalysisDto(
    string RepositoryId,
    string RepositoryFullName,
    string Branch,
    string CommitSha,
    DateTime AnalyzedAtUtc,
    IReadOnlyList<string> DetectedFiles,
    IReadOnlyList<SourceBuildPlanDto> Candidates);

public sealed record SourceBuildPlanDto(
    string CandidateKey,
    string Kind,
    string DisplayName,
    string SourceRoot,
    string ManifestPath,
    string Confidence,
    IReadOnlyList<string> Signals,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Commands,
    string? StartCommand,
    string DockerfilePreview);

public sealed record SourceRepositoryDto(
    string Id,
    string ConnectionId,
    string ExternalRepositoryId,
    string RepositoryFullName,
    string RepositoryUrl,
    bool IsPrivate,
    string Branch,
    string CommitSha,
    DateTime AnalyzedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    SourceBuildPlanDto SelectedPlan);

public static class GitProviderDtoExtensions
{
    public static GitProviderConnectionDto ToDto(this GitProviderConnection connection) =>
        new(
            connection.Id,
            connection.Provider.ToString(),
            connection.AccountLogin,
            connection.AccountType.ToString(),
            connection.RepositorySelection.ToString(),
            connection.CreatedAtUtc,
            connection.UpdatedAtUtc);

    public static SourceBuildPlanDto ToDto(this SourceBuildPlan plan) =>
        new(
            plan.CandidateKey,
            plan.Kind.ToString(),
            plan.DisplayName,
            plan.SourceRoot,
            plan.ManifestPath,
            plan.Confidence.ToString(),
            plan.Signals,
            plan.Warnings,
            plan.Commands,
            plan.StartCommand,
            plan.DockerfilePreview);

    public static SourceRepositoryDto ToDto(this SourceRepository repository) =>
        new(
            repository.Id,
            repository.ConnectionId,
            repository.ExternalRepositoryId,
            repository.RepositoryFullName,
            repository.RepositoryUrl,
            repository.IsPrivate,
            repository.Branch,
            repository.CommitSha,
            repository.AnalyzedAtUtc,
            repository.CreatedAtUtc,
            repository.UpdatedAtUtc,
            repository.SelectedPlan.ToDto());

    public static RepositoryAnalysisDto ToDto(
        this SourceRepositoryAnalysis analysis,
        ProviderRepository repository) =>
        new(
            repository.Id,
            repository.FullName,
            analysis.Branch,
            analysis.CommitSha,
            analysis.AnalyzedAtUtc,
            analysis.DetectedFiles,
            analysis.Candidates.Select(candidate => candidate.ToDto()).ToArray());
}

public sealed record ProviderRepository(
    string Id,
    string Name,
    string FullName,
    string HtmlUrl,
    bool IsPrivate,
    string DefaultBranch,
    DateTime? UpdatedAtUtc);

public sealed record ProviderBranch(
    string Name,
    string CommitSha,
    bool IsProtected);

public sealed record RepositorySourceSnapshot(
    ProviderRepository Repository,
    string Branch,
    string CommitSha,
    ImmutableList<string> Paths,
    ImmutableDictionary<string, string> FileContents,
    bool IsTruncated);
