using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Contracts;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Common;

public sealed class SourceRepositoryBuildFinder(LoadSourceRepository loadSourceRepository)
{
    public async Task<Result<SourceRepositoryBuildSnapshot, FindSourceRepositoryBuildError>>
        InvokeAsync(
            string tenantId,
            string sourceRepositoryId,
            CancellationToken cancellationToken = default)
    {
        var repository = await loadSourceRepository(tenantId, sourceRepositoryId, cancellationToken);
        return repository is null
            ? new Err<FindSourceRepositoryBuildError>(
                new(new SourceRepositoryBuildNotFound(sourceRepositoryId)))
            : new Ok<SourceRepositoryBuildSnapshot>(repository.ToBuildSnapshot());
    }
}
