using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record DeleteGitProviderConnectionRequest(
    [FromRoute] string ConnectionId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<string>, ITenantWriteOperation;

public sealed class DeleteGitProviderConnectionHandler(IGitProviderDbContext dbContext)
    : IMediatorHandler<DeleteGitProviderConnectionRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteGitProviderConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectionFilter = Builders<GitProviderConnection>.Filter.And(
            Builders<GitProviderConnection>.Filter.ForTenant(request.TenantId),
            Builders<GitProviderConnection>.Filter.Eq(item => item.Id, request.ConnectionId));
        if (!await dbContext.GitProviderConnections.Find(connectionFilter).AnyAsync(cancellationToken))
            return Failures.NotFound("Git provider connection not found.", request.ConnectionId);

        var linkFilter = Builders<SourceRepository>.Filter.And(
            Builders<SourceRepository>.Filter.ForTenant(request.TenantId),
            Builders<SourceRepository>.Filter.Eq(item => item.ConnectionId, request.ConnectionId));
        if (await dbContext.SourceRepositories.Find(linkFilter).AnyAsync(cancellationToken))
            return Failures.Conflict("Unlink source repositories before deleting this provider connection.");

        await dbContext.GitProviderConnections.DeleteOneAsync(connectionFilter, cancellationToken);
        return request.ConnectionId;
    }
}
