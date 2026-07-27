using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record GetIsolationScopeRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string ScopeId,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null)
    : IMediatorRequest<IsolationScopeDto>, ITenantReadOperation;

public sealed class GetIsolationScopeHandler(IResourceGroupDbContext dbContext)
    : IMediatorHandler<GetIsolationScopeRequest, IsolationScopeDto>
{
    public async Task<Result<IsolationScopeDto>> HandleAsync(
        GetIsolationScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = await dbContext.IsolationScopes
            .Find(item =>
                item.Id == request.ScopeId &&
                item.ResourceGroupId == request.ResourceGroupId &&
                item.TenantId == request.TenantId)
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
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Resource group ID must be a MongoDB ObjectId.");
        RuleFor(request => request.ScopeId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Isolation scope ID must be a MongoDB ObjectId.");
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
