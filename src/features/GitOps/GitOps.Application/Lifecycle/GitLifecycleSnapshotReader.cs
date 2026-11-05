using Pole.Features.GitOps.Application.Planning;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Lifecycle;

public sealed record GitLifecycleSnapshotRead(
    GitAcceptedSnapshot Snapshot,
    CompiledSnapshot Compiled,
    string DisplayUrl);

public sealed record GitPreviewReadFailed(string Code);

public union ReadLifecycleSnapshotOutcome(
    GitLifecycleSnapshotRead,
    GitPreviewReadFailed);

public sealed class GitLifecycleSnapshotReader(
    RepositoryProviderRegistry repositories,
    ManifestCompiler compiler,
    GitSnapshotAcceptance acceptance)
{
    public async Task<ReadLifecycleSnapshotOutcome> ReadAsync(
        RepositorySource source,
        GitRepositoryLocation location,
        IReadOnlyDictionary<ResourceAddress, string> selectedIds,
        CancellationToken ct)
    {
        var provider = repositories.Find(location.Provider);
        if (provider is null)
            return new GitPreviewReadFailed("RepositoryProviderUnavailable");

        var reference = new RepositorySourceReference(source.TenantId, location.ConnectionId,
            location.ExternalRepositoryId);
        var access = await provider.CheckAccessAsync(reference, ct);
        if (access is not RepositoryAccessGranted granted)
            return new GitPreviewReadFailed(access switch
            {
                RepositoryAccessDenied => nameof(RepositoryAccessDenied),
                RepositoryNotFound => nameof(RepositoryNotFound),
                RepositoryTemporarilyUnavailable => nameof(RepositoryTemporarilyUnavailable),
                RepositoryAccessGranted => throw new InvalidOperationException("Access was already handled.")
            });

        var resolution = await provider.ResolveCommitAsync(new(reference, location.Branch), ct);
        if (resolution is not RepositoryCommitResolved resolved)
            return new GitPreviewReadFailed(resolution switch
            {
                RepositoryAccessDenied => nameof(RepositoryAccessDenied),
                RepositoryNotFound => nameof(RepositoryNotFound),
                RepositoryRefNotFound => nameof(RepositoryRefNotFound),
                RepositoryTemporarilyUnavailable => nameof(RepositoryTemporarilyUnavailable),
                RepositoryCommitResolved => throw new InvalidOperationException("Resolution was already handled.")
            });

        var read = await provider.ReadSnapshotAsync(new(reference, resolved.Commit, location.ManifestDirectory), ct);
        if (read is not RepositorySnapshotRead snapshot)
            return new GitPreviewReadFailed(read switch
            {
                RepositoryAccessDenied => nameof(RepositoryAccessDenied),
                RepositoryNotFound => nameof(RepositoryNotFound),
                RepositoryRefNotFound => nameof(RepositoryRefNotFound),
                RepositoryDirectoryNotFound => nameof(RepositoryDirectoryNotFound),
                RepositorySnapshotRejected rejected => $"RepositorySnapshotRejected.{rejected.Reason}",
                RepositoryTemporarilyUnavailable => nameof(RepositoryTemporarilyUnavailable),
                RepositorySnapshotRead => throw new InvalidOperationException("Read was already handled.")
            });
        if (snapshot.Commit != resolved.Commit)
            return new GitPreviewReadFailed("RepositoryCommitMismatch");

        var compiled = compiler.Compile(snapshot.Files.Select(item => new ManifestFile(item.Path, item.Content))
            .ToArray());
        if (!compiled.Valid)
            return new GitPreviewReadFailed("SnapshotInvalid");

        return new GitLifecycleSnapshotRead(await acceptance.BuildAsync(new(source.TenantId,
            resolved.Commit, compiled, selectedIds), ct), compiled, granted.DisplayUrl);
    }
}
