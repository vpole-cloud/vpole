using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Features.GitProviders.Application.Persistence.Repositories;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;

namespace Pole.Features.GitProviders.Application.Common;

public sealed class SourceRepositoryArchiveDownloader(
    LoadSourceRepository loadSourceRepository,
    LoadGitProviderConnection loadGitProviderConnection,
    DownloadProviderRepositoryArchive downloadProviderRepositoryArchive)
{
    public async Task<DownloadSourceRepositoryArchiveOutcome> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        string commitSha,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        var repository = await loadSourceRepository(tenantId, sourceRepositoryId, cancellationToken);
        if (repository is null)
        {
            return new DownloadSourceRepositoryArchiveError(
                new SourceRepositoryBuildNotFound(sourceRepositoryId));
        }

        var connection = await loadGitProviderConnection(
            tenantId,
            repository.ConnectionId,
            cancellationToken);
        if (connection is null)
        {
            return new DownloadSourceRepositoryArchiveError(
                new SourceRepositoryBuildConnectionNotFound(
                    repository.Id,
                    repository.ConnectionId));
        }

        var downloaded = await downloadProviderRepositoryArchive(
            connection,
            repository.ExternalRepositoryId,
            commitSha,
            destination,
            cancellationToken);
        return downloaded switch
        {
            ProviderRepositoryArchiveDownloaded => new SourceRepositoryArchiveDownloaded(),
            GitProviderRepositoryAccessError error =>
                new DownloadSourceRepositoryArchiveError(error)
        };
    }
}
