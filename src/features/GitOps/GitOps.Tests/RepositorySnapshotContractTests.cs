using Pole.Features.GitOps.Contracts;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class RepositorySnapshotContractTests
{
    [Fact]
    public async Task Provider_reads_the_resolved_commit_and_empty_success_is_distinct_from_failure()
    {
        var provider = new SourceProvider();
        var repository = new RepositorySourceReference("tenant", "connection", "repository");
        var resolved = await provider.ResolveCommitAsync(
            new ResolveRepositoryCommitRequest(repository, "main"),
            default);
        var commit = Assert.IsType<RepositoryCommitResolved>(resolved.Value).Commit;
        var result = await provider.ReadSnapshotAsync(
            new ReadRepositorySnapshotRequest(repository, commit, "empty"),
            default);
        var snapshot = Assert.IsType<RepositorySnapshotRead>(result.Value);
        Assert.Equal(commit, snapshot.Commit);
        Assert.Empty(snapshot.Files);
        Assert.IsType<RepositoryDirectoryNotFound>(
            (await provider.ReadSnapshotAsync(
                new ReadRepositorySnapshotRequest(repository, commit, "missing"),
                default)).Value);
        Assert.IsType<RepositoryAccessDenied>(
            (await provider.ReadSnapshotAsync(
                new ReadRepositorySnapshotRequest(repository with { TenantId = "other" }, commit, "empty"),
                default)).Value);
    }

    private sealed class SourceProvider : IRepositorySnapshotProvider
    {
        public string Provider => "Example";

        public Task<CheckRepositoryAccessOutcome> CheckAccessAsync(
            RepositorySourceReference repository,
            CancellationToken cancellationToken) => Task.FromResult<CheckRepositoryAccessOutcome>(
            repository.TenantId == "tenant"
                ? new RepositoryAccessGranted("https://example.test/repository")
                : new RepositoryAccessDenied());

        public Task<ResolveRepositoryCommitOutcome> ResolveCommitAsync(
            ResolveRepositoryCommitRequest request,
            CancellationToken cancellationToken) => Task.FromResult<ResolveRepositoryCommitOutcome>(
            new RepositoryCommitResolved("immutable-sha"));

        public Task<ReadRepositorySnapshotOutcome> ReadSnapshotAsync(
            ReadRepositorySnapshotRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("immutable-sha", request.Commit);
            if (request.Repository.TenantId != "tenant")
                return Task.FromResult<ReadRepositorySnapshotOutcome>(new RepositoryAccessDenied());

            return Task.FromResult<ReadRepositorySnapshotOutcome>(
                request.ManifestDirectory == "empty"
                    ? new RepositorySnapshotRead(request.Commit, [])
                    : new RepositoryDirectoryNotFound(request.ManifestDirectory));
        }
    }
}
