using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Contracts;

namespace Pole.Tests.Integration.GitOps;

// Test-only immutable repository: changing content always creates a different full commit, even with the same label.
internal sealed class TestRepositorySnapshots : IRepositorySnapshotProvider
{
    public const string ConnectionId = "6b700099b213917a38c38001";
    public const string DisplayUrl = "https://example.test/repository";
    public string Provider => "TestRepository";
    private readonly ConcurrentDictionary<string, string> heads = new();

    private readonly ConcurrentDictionary<(string Repository, string Commit), RepositoryManifestFile[]> snapshots =
        new();

    public CheckRepositoryAccessOutcome Access { get; set; } = new RepositoryAccessGranted(DisplayUrl);
    public ConcurrentQueue<string> ReadCommits { get; } = new();

    public Func<ReadRepositorySnapshotRequest, CancellationToken, Task<ReadRepositorySnapshotOutcome>>? ReadOverride
    {
        get;
        set;
    }

    public string Publish(string repository, IReadOnlyList<ManifestFile> files, string label = "snapshot")
    {
        var commit = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Label = label, Files = files })))[..40];
        snapshots[(repository, commit)] = files.Select(item => new RepositoryManifestFile(item.Path, item.Content))
            .ToArray();
        heads[repository] = commit;
        return commit;
    }

    public Task<CheckRepositoryAccessOutcome> CheckAccessAsync(
        RepositorySourceReference repository,
        CancellationToken cancellationToken) => Task.FromResult(Access);

    public Task<ResolveRepositoryCommitOutcome> ResolveCommitAsync(
        ResolveRepositoryCommitRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<ResolveRepositoryCommitOutcome>(heads.TryGetValue(request.Repository.ExternalRepositoryId,
            out var commit)
            ? new RepositoryCommitResolved(commit)
            : new RepositoryRefNotFound(request.Branch));

    public Task<ReadRepositorySnapshotOutcome> ReadSnapshotAsync(
        ReadRepositorySnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ReadCommits.Enqueue(request.Commit);
        return ReadOverride is not null
            ? ReadOverride(request, cancellationToken)
            : Task.FromResult<ReadRepositorySnapshotOutcome>(snapshots.TryGetValue(
                (request.Repository.ExternalRepositoryId, request.Commit),
                out var files)
                ? new RepositorySnapshotRead(request.Commit, files)
                : new RepositoryRefNotFound(request.Commit));
    }
}
