using System.Collections.Immutable;
using Pole.Features.GitProviders.Application.Common;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class FakeGitHubAppClient : IGitHubAppClient
{
    public FakeGitHubAppClient()
    {
        const string project = """
                               <Project Sdk="Microsoft.NET.Sdk.Web">
                                 <PropertyGroup>
                                   <TargetFramework>net11.0</TargetFramework>
                                   <AssemblyName>Pole.Sample</AssemblyName>
                                 </PropertyGroup>
                               </Project>
                               """;
        Snapshot = new RepositorySourceSnapshot(
            Repository,
            "main",
            new string('a', 40),
            ["Pole.Sample.csproj", "Program.cs"],
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("Pole.Sample.csproj", project),
            false);
    }

    public byte[] ArchiveBytes { get; set; } = [];

    public GitHubInstallation Installation { get; set; } = new(
        "42",
        "1001",
        "lsbV",
        "User",
        "selected",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["contents"] = "read",
        });

    public ProviderRepository Repository { get; set; } = new(
        "9001",
        "pole-dotnet-sample",
        "lsbV/pole-dotnet-sample",
        "https://github.com/lsbV/pole-dotnet-sample",
        true,
        "main",
        new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc));

    public RepositorySourceSnapshot Snapshot { get; set; }

    public bool CanAccessInstallation { get; set; } = true;
    public GitHubApiException? RepositoryFailure { get; set; }
    public int RepositoryCalls { get; private set; }
    public string? LastCodeVerifier { get; private set; }
    public string? LastUserAccessToken { get; private set; }

    public Task<string> ResolveCommitAsync(GitHubResolveCommitRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Snapshot.CommitSha);

    public Task<GitHubCommitTree> ReadCommitTreeAsync(
        GitHubReadTreeRequest request,
        CancellationToken cancellationToken)
    {
        var directories = Snapshot.Paths
            .SelectMany(path =>
                Enumerable.Range(1, path.Split('/').Length - 1)
                    .Select(count => string.Join('/', path.Split('/').Take(count))))
            .Distinct(StringComparer.Ordinal);
        return Task.FromResult(
            new GitHubCommitTree(
                directories.Select(path => new GitHubTreeEntry(path, "tree", "040000"))
                    .Concat(Snapshot.Paths.Select(path => new GitHubTreeEntry(path, "blob", "100644")))
                    .ToArray(),
                Snapshot.IsTruncated));
    }

    public Task<GitHubInstallation> GetInstallationAsync(
        string installationId,
        CancellationToken cancellationToken = default) => Task.FromResult(Installation);

    public Task<string> ExchangeOAuthCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        LastCodeVerifier = codeVerifier;
        return Task.FromResult("discarded-user-access-token");
    }

    public Task<bool> UserCanAccessInstallationAsync(
        string userAccessToken,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        LastUserAccessToken = userAccessToken;
        return Task.FromResult(CanAccessInstallation);
    }

    public Task<ProviderPage<ProviderRepository>> GetRepositoriesAsync(
        string installationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        RepositoryCalls++;
        if (RepositoryFailure is not null)
            throw RepositoryFailure;
        return Task.FromResult(new ProviderPage<ProviderRepository>([Repository], page, pageSize, false));
    }

    public Task<ProviderRepository> GetRepositoryAsync(
        string installationId,
        string repositoryId,
        CancellationToken cancellationToken = default) => Task.FromResult(Repository);

    public Task<ProviderPage<ProviderBranch>> GetBranchesAsync(
        string installationId,
        string repositoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) => Task.FromResult(
        new ProviderPage<ProviderBranch>(
            [new ProviderBranch("main", Snapshot.CommitSha, true)],
            page,
            pageSize,
            false));

    public Task<RepositorySourceSnapshot> GetRepositorySnapshotAsync(
        string installationId,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken = default) => Task.FromResult(Snapshot with { Branch = branch });

    public Task DownloadRepositoryArchiveAsync(
        string installationId,
        string repositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        destination.WriteAsync(ArchiveBytes, cancellationToken).AsTask();
}
