using System.Net;
using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.GitProviders.Application.Snapshots;

internal sealed class GitHubRepositorySnapshotProvider(
    LoadTenantIdentity loadTenant,
    LoadGitProviderConnection loadConnection,
    IGitHubAppClient client,
    RepositoryManifestArchiveReader reader,
    RepositoryArchiveOptions options) : IRepositorySnapshotProvider
{
    public string Provider => nameof(GitProviderKind.GitHub);

    public async Task<ResolveRepositoryCommitOutcome> ResolveCommitAsync(
        ResolveRepositoryCommitRequest request,
        CancellationToken cancellationToken)
    {
        var access = await LoadAccessAsync(request.Repository, cancellationToken);
        if (access is not GitHubRepositoryAccess repository)
            return access switch
            {
                RepositoryAccessDenied denied => denied,
                RepositoryNotFound missing => missing,
                RepositoryTemporarilyUnavailable temporary => temporary,
                GitHubRepositoryAccess => throw new InvalidOperationException("Repository access was already handled.")
            };

        try
        {
            var commit = await client.ResolveCommitAsync(new(repository, request.Branch), cancellationToken);
            return IsCommit(commit)
                ? new RepositoryCommitResolved(commit.ToLowerInvariant())
                : new RepositoryTemporarilyUnavailable();
        }
        catch (GitHubApiException exception) when (AccessDenied(exception))
        {
            return new RepositoryAccessDenied();
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new RepositoryRefNotFound(request.Branch);
        }
        catch (Exception exception) when (Temporary(exception, cancellationToken))
        {
            return new RepositoryTemporarilyUnavailable();
        }
    }

    public async Task<ReadRepositorySnapshotOutcome> ReadSnapshotAsync(
        ReadRepositorySnapshotRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsCommit(request.Commit))
            return new RepositorySnapshotRejected(RepositorySnapshotRejection.InvalidCommit);

        if (request.ManifestDirectory.Length > 0 && !RepositoryPaths.Safe(request.ManifestDirectory))
            return new RepositorySnapshotRejected(RepositorySnapshotRejection.InvalidPath);

        var access = await LoadAccessAsync(request.Repository, cancellationToken);
        if (access is not GitHubRepositoryAccess repository)
            return access switch
            {
                RepositoryAccessDenied denied => denied,
                RepositoryNotFound missing => missing,
                RepositoryTemporarilyUnavailable temporary => temporary,
                GitHubRepositoryAccess => throw new InvalidOperationException("Repository access was already handled.")
            };

        try
        {
            var tree = await client.ReadCommitTreeAsync(new(repository, request.Commit), cancellationToken);
            if (tree.Truncated || tree.Entries.Count > options.MaxEntries)
                return new RepositorySnapshotRejected(RepositorySnapshotRejection.SizeLimit);

            var path = Path.Combine(Path.GetTempPath(), $"pole-git-snapshot-{Guid.NewGuid():N}.tar.gz");
            // DeleteOnClose covers cancellation and failed downloads. Nothing is extracted into the workspace.
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using var archive = new FileStream(path, fileOptions);
            using var limited = new ArchiveLimitStream(archive, options.MaxCompressedBytes);
            await client.DownloadRepositoryArchiveAsync(repository.InstallationId, repository.Repository.Id,
                request.Commit, limited, cancellationToken);
            await archive.FlushAsync(cancellationToken);
            archive.Position = 0;
            return await reader.ReadAsync(new(archive, request.Commit, request.ManifestDirectory, tree),
                cancellationToken);
        }
        catch (ArchiveSizeLimitException)
        {
            return new RepositorySnapshotRejected(RepositorySnapshotRejection.SizeLimit);
        }
        catch (GitHubUnsafeRedirectException)
        {
            return new RepositorySnapshotRejected(RepositorySnapshotRejection.UnsafeArchive);
        }
        catch (GitHubApiException exception) when (AccessDenied(exception))
        {
            return new RepositoryAccessDenied();
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return exception.Target == GitHubRequestTarget.Repository
                ? new RepositoryNotFound()
                : new RepositoryRefNotFound(request.Commit);
        }
        catch (Exception exception) when (Temporary(exception, cancellationToken))
        {
            return new RepositoryTemporarilyUnavailable();
        }
    }

    private async Task<RepositoryAccessOutcome> LoadAccessAsync(
        RepositorySourceReference reference,
        CancellationToken ct)
    {
        if (await loadTenant(reference.TenantId, ct) is TenantIdentityNotFound)
            return new RepositoryAccessDenied();

        var connection = await loadConnection(reference.TenantId, reference.ConnectionId, ct);
        if (connection is null || connection.Provider != GitProviderKind.GitHub)
            return new RepositoryAccessDenied();

        try
        {
            var repository = await client.GetRepositoryAsync(connection.ExternalInstallationId,
                reference.ExternalRepositoryId, ct);
            return new GitHubRepositoryAccess(connection.ExternalInstallationId, repository);
        }
        catch (GitHubApiException exception) when (AccessDenied(exception))
        {
            return new RepositoryAccessDenied();
        }
        catch (GitHubApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new RepositoryNotFound();
        }
        catch (Exception exception) when (Temporary(exception, ct))
        {
            return new RepositoryTemporarilyUnavailable();
        }
    }

    private static bool IsCommit(string commit) => commit is { Length: 40 } && commit.All(char.IsAsciiHexDigit);

    private static bool AccessDenied(GitHubApiException exception) => !exception.IsRateLimited &&
        (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
         exception.StatusCode == HttpStatusCode.NotFound && exception.Target == GitHubRequestTarget.Installation);

    private static bool Temporary(Exception exception, CancellationToken ct) =>
        exception is GitHubApiException or HttpRequestException or JsonException ||
        exception is OperationCanceledException && !ct.IsCancellationRequested;
}

internal union RepositoryAccessOutcome(
    GitHubRepositoryAccess,
    RepositoryAccessDenied,
    RepositoryNotFound,
    RepositoryTemporarilyUnavailable);
