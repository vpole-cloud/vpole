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
        DownloadSourceRepositoryArchiveRequest request,
        CancellationToken cancellationToken = default)
    {
        var repository = await loadSourceRepository(request.TenantId, request.SourceRepositoryId, cancellationToken);
        if (repository is null)
        {
            return new DownloadSourceRepositoryArchiveError(
                new SourceRepositoryBuildNotFound(request.SourceRepositoryId));
        }

        var connection = await loadGitProviderConnection(
            request.TenantId,
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
            request.CommitSha,
            request.Destination,
            cancellationToken);
        return downloaded switch
        {
            ProviderRepositoryArchiveDownloaded => new SourceRepositoryArchiveDownloaded(),
            GitProviderRepositoryAccessError error =>
                new DownloadSourceRepositoryArchiveError(error)
        };
    }
}
