using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Endpoints.Repositories;
using Pole.Features.GitProviders.Application.Endpoints.SourceRepositories;
using Pole.Features.GitProviders.Application.Repositories;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Xunit;
using AnalyzeOutcome = Pole.Features.GitProviders.Application.ProviderOperations.AnalyzeProviderRepositoryOutcome;

namespace Pole.Features.GitProviders.Tests;

public sealed class SourceRepositoryIdentityTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly ProviderRepository Remote = new(
        "42", "application", "organization/application", "https://github.test/organization/application",
        true, "main", null);

    [Fact]
    public void FactoryCreatesIndependentIdentityAndExplicitFencesWithoutBuildMetadata()
    {
        var repository = Repository();
        Assert.Equal(1, repository.Revision);
        Assert.Equal(0, repository.ReferenceFence);
        Assert.Equal(repository, BsonSerializer.Deserialize<SourceRepository>(repository.ToBson()));
        var bson = repository.ToBsonDocument();
        Assert.DoesNotContain("CandidateKey", bson.Names);
        Assert.DoesNotContain("SelectedPlan", bson.Names);
        Assert.DoesNotContain("CommitSha", bson.Names);
        Assert.DoesNotContain("AnalyzedAtUtc", bson.Names);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UpperCase")]
    [InlineData("invalid/name")]
    [InlineData("trailing-")]
    public void FactoryRejectsInvalidTechnicalNames(string name) =>
        Assert.Throws<ArgumentException>(() => Repository(name));

    [Fact]
    public async Task LinkUsesAccessAndRefResolutionWithoutAnalysisOrCandidate()
    {
        SourceRepository? persisted = null;
        var handler = new LinkProviderRepositoryHandler(
            (request, _) =>
            {
                Assert.Equal(new ResolveProviderRepositoryRequest("tenant", "connection", "42", "feature/api"),
                    request);
                return Task.FromResult<ResolveProviderRepositoryOutcome>(
                    new ResolvedProviderRepository(Remote, Commit));
            },
            (repository, _) =>
            {
                persisted = repository;
                return Task.FromResult<PersistLinkedSourceRepositoryOutcome>(repository);
            });
        var result = await handler.HandleAsync(new("connection", "42", "tenant", new("application", "feature/api")));
        var dto = Assert.IsType<SourceRepositoryDto>(result.Value);
        Assert.Equal("application", dto.TechnicalName);
        Assert.Equal("feature/api", dto.Branch);
        Assert.Equal("42", persisted!.ExternalRepositoryId);
    }

    [Fact]
    public async Task FailedResolveDoesNotPersistAnIdentity()
    {
        var handler = new LinkProviderRepositoryHandler(
            (_, _) => Task.FromResult<ResolveProviderRepositoryOutcome>(new SourceRepositoryBranchNotFound("missing")),
            (_, _) => throw new InvalidOperationException("Persistence must not run after failed resolution."));
        var result = await handler.HandleAsync(new("connection", "42", "tenant", new("application", "missing")));
        Assert.IsType<SourceRepositoryBranchNotFound>(result.Value);
    }

    [Fact]
    public async Task BuildCommitIsResolvedFreshInsteadOfReadingAnAnalyzedCommit()
    {
        var sha = Commit;
        var repository = Repository();
        var resolver = new SourceRepositoryCommitResolver(
            (tenant, id, _) => Task.FromResult<SourceRepository?>(
                tenant == repository.TenantId && id == repository.Id ? repository : null),
            (_, _) => Task.FromResult<ResolveProviderRepositoryOutcome>(new ResolvedProviderRepository(Remote, sha)));
        var first = Assert.IsType<SourceRepositoryCommitResolved>(
            (await resolver.InvokeAsync(new("tenant", repository.Id), default)).Value);
        sha = new string('b', 40);
        var second = Assert.IsType<SourceRepositoryCommitResolved>(
            (await resolver.InvokeAsync(new("tenant", repository.Id), default)).Value);
        Assert.Equal(Commit, first.CommitSha);
        Assert.Equal(sha, second.CommitSha);
        Assert.Equal(first.Repository, second.Repository);
        Assert.IsType<SourceRepositoryBuildNotFound>(
            (await resolver.InvokeAsync(new("foreign", repository.Id), default)).Value);
    }

    [Fact]
    public async Task ReanalysisReturnsAuthoringCandidatesWithoutChangingIdentity()
    {
        var repository = Repository();
        var analysis = new SourceRepositoryAnalysis(repository.Branch, Commit, DateTime.UnixEpoch, [], []);
        var connection = GitProviderConnectionFactory.Create("tenant", "github", GitProviderKind.GitHub,
            "installation", "account", "organization", GitProviderAccountType.Organization,
            GitProviderRepositorySelection.All, "creator", GitProviderConnectionFactory.DefaultOptions);
        var handler = new ReanalyzeSourceRepositoryHandler(
            (_, _, _) => Task.FromResult<SourceRepository?>(repository),
            (_, _, _, _, _) => Task.FromResult<AnalyzeOutcome>(
                new AnalyzedProviderRepository(connection, Remote, analysis)));
        var result = Assert.IsType<RepositoryAnalysisDto>(
            (await handler.HandleAsync(new(repository.Id, repository.TenantId))).Value);
        Assert.Empty(result.Candidates);
        Assert.Equal(Commit, result.CommitSha);
        Assert.Equal(1, repository.Revision);
    }

    private static SourceRepository Repository(string name = "application") => SourceRepositoryFactory.Create(
        "tenant", name, "connection", Remote.Id, Remote.FullName, Remote.HtmlUrl, Remote.IsPrivate, "main",
        SourceRepositoryFactory.DefaultOptions with
        {
            CreatedAtUtc = DateTime.UnixEpoch,
            UpdatedAtUtc = DateTime.UnixEpoch
        });
}
