using FluentValidation;
using Pole.Validation;
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

public sealed record GetIsolationScopeRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string ScopeId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IsolationScopeDto>, ITenantReadOperation;

public sealed class GetIsolationScopeHandler(IResourceGroupDbContext dbContext)
    : IMediatorHandler<GetIsolationScopeRequest, IsolationScopeDto>
{
    public async Task<Result<IsolationScopeDto>> HandleAsync(
        GetIsolationScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(Builders<ResourceGroup>.Filter.And(
                Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
                Builders<ResourceGroup>.Filter.Eq(
                    group => group.Id,
                    request.ResourceGroupId)))
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
            return Failures.NotFound("Resource group not found.", request.ResourceGroupId);

        var scope = await dbContext.IsolationScopes
            .Find(item =>
                item.Id == request.ScopeId &&
                item.ResourceGroupId == resourceGroup.Id &&
                item.TenantId == resourceGroup.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        return scope is null
            ? Failures.NotFound("Isolation scope not found.", request.ScopeId)
            : scope.ToDto();
    }
}

public sealed class GetIsolationScopeRequestValidator
    : AbstractValidator<GetIsolationScopeRequest>
{
    public GetIsolationScopeRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId)
            .MongoObjectId("Resource group ID");
        RuleFor(request => request.ScopeId)
            .MongoObjectId("Isolation scope ID");
    }
}

public static class GetIsolationScopeEndpoint
{
    public static IEndpointRouteBuilder MapGetIsolationScopeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ResourceGroupId}/isolation-scopes/{ScopeId}", async (
                IMediator mediator,
                [AsParameters] GetIsolationScopeRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetIsolationScope")
            .WithSummary("Gets an isolation scope owned by a resource group.")
            .Produces<IsolationScopeDto>();

        return app;
    }
}
