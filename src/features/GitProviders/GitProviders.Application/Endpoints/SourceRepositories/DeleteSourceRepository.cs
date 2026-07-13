using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record DeleteSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<string>, ITenantWriteOperation;

public sealed class DeleteSourceRepositoryHandler(
    IGitProviderDbContext dbContext,
    ISourceBuildReferenceLookup sourceBuildReferences)
    : IMediatorHandler<DeleteSourceRepositoryRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteSourceRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var hasConfiguration = await sourceBuildReferences.HasConfigurationForSourceRepositoryAsync(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (!hasConfiguration.TryGetValue(out var exists, out var lookupFailure))
            return lookupFailure;
        if (exists)
        {
            return Failures.Conflict("Delete the source build configuration before unlinking this repository.");
        }

        var result = await dbContext.SourceRepositories.DeleteOneAsync(
            Builders<SourceRepository>.Filter.And(
                Builders<SourceRepository>.Filter.ForTenant(request.TenantId),
                Builders<SourceRepository>.Filter.Eq(item => item.Id, request.Id)),
            cancellationToken);
        return result.DeletedCount == 0
            ? Failures.NotFound("Source repository not found.", request.Id)
            : request.Id;
    }
}
