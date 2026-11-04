using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.GitProviders.Application.Snapshots;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Tenants.Contracts;
using Xunit;
using static Pole.Features.GitProviders.Tests.RepositoryManifestArchiveReaderTests;

namespace Pole.Features.GitProviders.Tests;

public sealed class GitHubRepositorySnapshotProviderTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly RepositorySourceReference Reference = new("tenant", "connection", "9001");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsYamlOnlyRepositoryWithoutBuildAnalysis(bool isPrivate)
    {
        var fixture = new AdapterFixture { IsPrivate = isPrivate };
        var provider = fixture.Provider();
        var resolved = await provider.ResolveCommitAsync(new(Reference, "main"), default);
        Assert.Equal(Commit, Assert.IsType<RepositoryCommitResolved>(resolved.Value).Commit);
        var snapshot = Assert.IsType<RepositorySnapshotRead>((await provider.ReadSnapshotAsync(
            new(Reference, Commit, "manifests"), default)).Value);
        Assert.Equal("kind: ResourceGroup", Assert.Single(snapshot.Files).Content);
        Assert.DoesNotContain(fixture.Paths, path => path.Contains("/contents/", StringComparison.Ordinal));
        Assert.Contains($"/repos/org/sample/tarball/{Commit}", fixture.Paths);
    }

    [Fact]
    public async Task TenantAndConnectionAccessIsRecheckedAfterResolveWithoutAuthorMembership()
    {
        var fixture = new AdapterFixture();
        var provider = fixture.Provider();
        Assert.IsType<RepositoryCommitResolved>(
            (await provider.ResolveCommitAsync(new(Reference, "main"), default)).Value);
        fixture.Connection = null;
        var calls = fixture.Paths.Count;
        Assert.IsType<RepositoryAccessDenied>((await provider.ReadSnapshotAsync(
            new(Reference, Commit, "manifests"), default)).Value);
        Assert.Equal(calls, fixture.Paths.Count);
        Assert.IsType<RepositoryAccessDenied>((await provider.ResolveCommitAsync(
            new(Reference with { TenantId = "foreign" }, "main"), default)).Value);
        Assert.Equal(calls, fixture.Paths.Count);
    }

    [Theory]
    [InlineData("main", "manifests", RepositorySnapshotRejection.InvalidCommit)]
    [InlineData(Commit, "../escape", RepositorySnapshotRejection.InvalidPath)]
    public async Task InvalidInputDoesNotMakeProviderRequests(
        string commit,
        string directory,
        RepositorySnapshotRejection reason)
    {
        var fixture = new AdapterFixture();
        var result = await fixture.Provider().ReadSnapshotAsync(new(Reference, commit, directory), default);
        Assert.Equal(reason, Assert.IsType<RepositorySnapshotRejected>(result.Value).Reason);
        Assert.Empty(fixture.Paths);
    }

    [Theory]
    [InlineData("/access_tokens", 404, typeof(RepositoryAccessDenied))]
    [InlineData("/repositories/9001", 404, typeof(RepositoryNotFound))]
    [InlineData("/repositories/9001", 403, typeof(RepositoryAccessDenied))]
    [InlineData("/repositories/9001", 429, typeof(RepositoryTemporarilyUnavailable))]
    [InlineData("/branches/main", 404, typeof(RepositoryRefNotFound))]
    [InlineData("/branches/main", 503, typeof(RepositoryTemporarilyUnavailable))]
    public async Task ResolveRetainsExpectedFailureMeaning(string suffix, int status, Type expected)
    {
        var fixture = new AdapterFixture { FailureSuffix = suffix, FailureStatus = (HttpStatusCode)status };
        var outcome = await fixture.Provider().ResolveCommitAsync(new(Reference, "main"), default);
        Assert.IsType(expected, outcome.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsAreRemovedOnSuccessAndFailure(bool oversized)
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "pole-git-snapshot-*.tar.gz");
        var fixture = new AdapterFixture();
        var options = new RepositoryArchiveOptions { MaxCompressedBytes = oversized ? 1 : 100 * 1024 * 1024 };
        var outcome = await fixture.Provider(options).ReadSnapshotAsync(new(Reference, Commit, "manifests"), default);
        if (oversized)
            Assert.Equal(RepositorySnapshotRejection.SizeLimit,
                Assert.IsType<RepositorySnapshotRejected>(outcome.Value).Reason);
        else
            Assert.IsType<RepositorySnapshotRead>(outcome.Value);

        Assert.Equal(before.Order(), Directory.GetFiles(Path.GetTempPath(), "pole-git-snapshot-*.tar.gz").Order());
    }

    [Fact]
    public async Task TimeoutIsTemporaryButCallerCancellationPropagates()
    {
        var fixture = new AdapterFixture { Timeout = true };
        Assert.IsType<RepositoryTemporarilyUnavailable>((await fixture.Provider()
            .ResolveCommitAsync(new(Reference, "main"), default)).Value);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider()
            .ResolveCommitAsync(new(Reference, "main"), cancellation.Token));
    }

    [Fact]
    public void InvalidArchiveOptionsAreRejectedByOptionsValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["GitHubRepositoryArchive:MaxEntries"] = "0" }).Build();
        using var services = new ServiceCollection().AddGitProvidersFeature(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<RepositoryArchiveOptions>>().Value);
    }

    [Theory]
    [InlineData("/access_tokens", 404, typeof(SourceRepositoryAccessDenied))]
    [InlineData("/repositories/9001", 404, typeof(SourceRepositoryRemoteNotFound))]
    [InlineData("/repositories/9001", 403, typeof(SourceRepositoryAccessDenied))]
    [InlineData("/repositories/9001", 429, typeof(SourceRepositoryUnavailable))]
    [InlineData("/branches/main", 404, typeof(SourceRepositoryBranchNotFound))]
    [InlineData("/branches/main", 503, typeof(SourceRepositoryUnavailable))]
    public async Task SourceIdentityResolutionRetainsExpectedFailureMeaning(string suffix, int status, Type expected)
    {
        var fixture = new AdapterFixture { FailureSuffix = suffix, FailureStatus = (HttpStatusCode)status };
        var result = await fixture.RepositoryResolver()
            .InvokeAsync(new("tenant", "connection", "9001", "main"), default);
        Assert.IsType(expected, result.Value);
    }

    [Fact]
    public async Task SourceIdentityResolutionReadsMetadataAndCommitWithoutTreeOrAnalysis()
    {
        var fixture = new AdapterFixture();
        var result = await fixture.RepositoryResolver()
            .InvokeAsync(new("tenant", "connection", "9001", "main"), default);
        Assert.Equal(Commit, Assert.IsType<ResolvedProviderRepository>(result.Value).CommitSha);
        Assert.DoesNotContain(fixture.Paths, path => path.Contains("/git/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Paths, path => path.Contains("/contents/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Paths, path => path.Contains("/tarball/", StringComparison.Ordinal));
        fixture.Connection = null;
        var count = fixture.Paths.Count;
        Assert.IsType<GitProviderConnectionNotFound>((await fixture.RepositoryResolver()
            .InvokeAsync(new("tenant", "connection", "9001", "main"), default)).Value);
        Assert.Equal(count, fixture.Paths.Count);
    }

    [Fact]
    public async Task SourceIdentityResolutionDistinguishesTimeoutFromCallerCancellation()
    {
        var fixture = new AdapterFixture { Timeout = true };
        Assert.IsType<SourceRepositoryUnavailable>((await fixture.RepositoryResolver()
            .InvokeAsync(new("tenant", "connection", "9001", "main"), default)).Value);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RepositoryResolver()
            .InvokeAsync(new("tenant", "connection", "9001", "main"), cancellation.Token));
    }

    [Fact]
    public async Task PromotionAccessChecksCurrentAuthorityWithoutResolvingOrDownloadingThePinnedInput()
    {
        var fixture = new AdapterFixture();
        var checker = fixture.BuildAccessChecker();
        var request = new SourceRepositoryReferenceRequest("tenant", fixture.BuildRepository.Id);
        Assert.IsType<SourceRepositoryBuildSnapshot>((await checker.InvokeAsync(request, default)).Value);
        Assert.Contains("/repositories/9001", fixture.Paths);
        Assert.DoesNotContain(fixture.Paths, path => path.Contains("/branches/", StringComparison.Ordinal) ||
            path.Contains("/tarball/", StringComparison.Ordinal) || path.Contains("/git/", StringComparison.Ordinal));
        var calls = fixture.Paths.Count;
        fixture.Connection = null;
        Assert.IsType<SourceRepositoryBuildConnectionNotFound>((await checker.InvokeAsync(request, default)).Value);
        Assert.Equal(calls, fixture.Paths.Count);
        fixture.TenantPresent = false;
        Assert.IsType<SourceRepositoryAccessDenied>((await checker.InvokeAsync(request, default)).Value);
        Assert.IsType<SourceRepositoryBuildNotFound>((await checker.InvokeAsync(
            request with { TenantId = "foreign" }, default)).Value);
        Assert.Equal(calls, fixture.Paths.Count);
    }

    [Theory]
    [InlineData("/access_tokens", 404, typeof(SourceRepositoryAccessDenied))]
    [InlineData("/repositories/9001", 404, typeof(SourceRepositoryRemoteNotFound))]
    [InlineData("/repositories/9001", 403, typeof(SourceRepositoryAccessDenied))]
    [InlineData("/repositories/9001", 429, typeof(SourceRepositoryUnavailable))]
    [InlineData("/repositories/9001", 503, typeof(SourceRepositoryUnavailable))]
    public async Task PromotionAccessRetainsExpectedFailureMeaning(string suffix, int status, Type expected)
    {
        var fixture = new AdapterFixture { FailureSuffix = suffix, FailureStatus = (HttpStatusCode)status };
        var outcome = await fixture.BuildAccessChecker().InvokeAsync(
            new("tenant", fixture.BuildRepository.Id), default);
        Assert.IsType(expected, outcome.Value);
    }

    [Fact]
    public async Task PromotionAccessPreservesCancellationAndClassifiesTimeout()
    {
        var fixture = new AdapterFixture { Timeout = true };
        var checker = fixture.BuildAccessChecker();
        var request = new SourceRepositoryReferenceRequest("tenant", fixture.BuildRepository.Id);
        Assert.IsType<SourceRepositoryUnavailable>((await checker.InvokeAsync(request, default)).Value);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.InvokeAsync(request, cancellation.Token));
    }

    private sealed class AdapterFixture : HttpMessageHandler
    {
        public GitProviderConnection? Connection { get; set; } = GitProviderConnectionFactory.Create(
            "tenant", "production-git", GitProviderKind.GitHub, "42", "account", "display",
            GitProviderAccountType.User, GitProviderRepositorySelection.All, "departed-author",
            GitProviderConnectionFactory.DefaultOptions);
        public bool TenantPresent { get; set; } = true;
        public SourceRepository BuildRepository { get; } = SourceRepositoryFactory.Create(
            "tenant", "application", "connection", "9001", "org/sample", "https://github.test/org/sample", true,
            "main", SourceRepositoryFactory.DefaultOptions);
        public bool IsPrivate { get; init; } = true;
        public string? FailureSuffix { get; init; }
        public HttpStatusCode FailureStatus { get; init; }
        public bool Timeout { get; init; }
        public List<string> Paths { get; } = [];

        public GitHubRepositorySnapshotProvider Provider(RepositoryArchiveOptions? options = null)
        {
            var settings = options ?? new RepositoryArchiveOptions();
            var client = new GitHubAppClient(new HttpClient(this), new GitHubAppOptions
            {
                ApiBaseUrl = "https://api.test"
            }, () => "jwt", new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
            return new GitHubRepositorySnapshotProvider(
                (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(tenant == "tenant"
                    ? new TenantIdentitySnapshot(tenant, "Tenant") : new TenantIdentityNotFound(tenant)),
                (tenant, _, _) => Task.FromResult(tenant == "tenant" ? Connection : null),
                client, new RepositoryManifestArchiveReader(settings), settings);
        }

        public GitHubResolveProviderRepository RepositoryResolver()
        {
            var client = new GitHubAppClient(new HttpClient(this), new GitHubAppOptions
            {
                ApiBaseUrl = "https://api.test"
            }, () => "jwt", new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
            return new GitHubResolveProviderRepository(
                (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(tenant == "tenant"
                    ? new TenantIdentitySnapshot(tenant, "Tenant") : new TenantIdentityNotFound(tenant)),
                (tenant, _, _) => Task.FromResult(tenant == "tenant" ? Connection : null), client);
        }

        public SourceRepositoryBuildAccessChecker BuildAccessChecker()
        {
            var client = new GitHubAppClient(new HttpClient(this), new GitHubAppOptions
            {
                ApiBaseUrl = "https://api.test"
            }, () => "jwt", new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
            return new SourceRepositoryBuildAccessChecker(
                (tenant, id, _) => Task.FromResult<SourceRepository?>(
                    tenant == BuildRepository.TenantId && id == BuildRepository.Id ? BuildRepository : null),
                (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(TenantPresent
                    ? new TenantIdentitySnapshot(tenant, "Tenant") : new TenantIdentityNotFound(tenant)),
                (tenant, _, _) => Task.FromResult(tenant == "tenant" ? Connection : null), client);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Timeout)
                throw new TaskCanceledException("HTTP timeout");

            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (FailureSuffix is not null && path.EndsWith(FailureSuffix, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(FailureStatus));

            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
                return Json("""{"token":"installation","expires_at":"2099-01-01T00:00:00Z"}""");
            if (path == "/repositories/9001")
                return Json($$"""
                    {"id":9001,"name":"sample","full_name":"org/sample","html_url":"https://github.com/org/sample",
                    "private":{{IsPrivate.ToString().ToLowerInvariant()}},"default_branch":"main","updated_at":null}
                    """);
            if (path.EndsWith("/branches/main", StringComparison.Ordinal))
                return Json($$$"""{"name":"main","commit":{"sha":"{{{Commit}}}"}}""");
            if (path.Contains("/git/commits/", StringComparison.Ordinal))
                return Json("""{"tree":{"sha":"tree"}}""");
            if (path.Contains("/git/trees/", StringComparison.Ordinal))
                return Json("""
                    {"tree":[{"path":"manifests","type":"tree","mode":"040000"},
                    {"path":"manifests/a.yaml","type":"blob","mode":"100644"}],"truncated":false}
                    """);
            if (path.EndsWith($"/tarball/{Commit}", StringComparison.Ordinal))
            {
                using var archive = Archive(File("root/manifests/a.yaml", "kind: ResourceGroup"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archive.ToArray())
                });
            }

            throw new InvalidOperationException($"Unexpected test request: {path}");
        }

        private static Task<HttpResponseMessage> Json(string json) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
