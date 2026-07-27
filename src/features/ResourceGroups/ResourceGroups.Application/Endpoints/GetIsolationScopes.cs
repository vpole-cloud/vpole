using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record GetIsolationScopesRequest(
    [FromRoute] string ResourceGroupId,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null)
    : IMediatorRequest<IReadOnlyList<IsolationScopeDto>>, ITenantReadOperation;

public sealed class GetIsolationScopesHandler(IResourceGroupDbContext dbContext)
    : IMediatorHandler<GetIsolationScopesRequest, IReadOnlyList<IsolationScopeDto>>
{
    public async Task<Result<IReadOnlyList<IsolationScopeDto>>> HandleAsync(
        GetIsolationScopesRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroupExists = await dbContext.ResourceGroups
            .Find(Builders<ResourceGroup>.Filter.And(
                Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
                Builders<ResourceGroup>.Filter.Eq(group => group.Id, request.ResourceGroupId)))
            .AnyAsync(cancellationToken);
        if (!resourceGroupExists)
            return Failures.NotFound("Resource group not found.", request.ResourceGroupId);

        var scopes = await dbContext.IsolationScopes
            .Find(scope =>
                scope.TenantId == request.TenantId &&
                scope.ResourceGroupId == request.ResourceGroupId)
            .SortByDescending(scope => scope.IsDefault)
            .ThenBy(scope => scope.NormalizedName)
            .ToListAsync(cancellationToken);

        return scopes.Select(scope => scope.ToDto()).ToList();
    }
}

public sealed class GetIsolationScopesRequestValidator
    : AbstractValidator<GetIsolationScopesRequest>
{
    public GetIsolationScopesRequestValidator() =>
        RuleFor(request => request.ResourceGroupId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Resource group ID must be a MongoDB ObjectId.");
}

public static class GetIsolationScopesEndpoint
{
    public static IEndpointRouteBuilder MapGetIsolationScopesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ResourceGroupId}/isolation-scopes", async (
                IMediator mediator,
                [AsParameters] GetIsolationScopesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetIsolationScopes")
            .WithSummary("Lists isolation scopes owned by a resource group.")
            .Produces<IReadOnlyList<IsolationScopeDto>>();

        return app;
    }
}
