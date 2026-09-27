using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record GetIsolationScopesRequest(
    [FromRoute] string ResourceGroupId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetIsolationScopesOutcome>, ITenantReadOperation;

public union GetIsolationScopesOutcome(
    IReadOnlyList<IsolationScopeDto>,
    ResourceGroupNotFound);

public sealed class GetIsolationScopesHandler(
    LoadResourceGroup loadResourceGroup,
    LoadIsolationScopes loadIsolationScopes)
    : IMediatorOutcomeHandler<GetIsolationScopesRequest, GetIsolationScopesOutcome>
{
    public async Task<GetIsolationScopesOutcome> HandleAsync(
        GetIsolationScopesRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await loadResourceGroup(
            request.TenantId,
            request.ResourceGroupId,
            cancellationToken);
        if (resourceGroup is null)
            return new ResourceGroupNotFound(request.ResourceGroupId);

        var scopes = await loadIsolationScopes(
            resourceGroup.TenantId,
            resourceGroup.Id,
            cancellationToken);

        return scopes.Select(scope => scope.ToDto()).ToList();
    }
}

public sealed class GetIsolationScopesRequestValidator
    : AbstractValidator<GetIsolationScopesRequest>
{
    public GetIsolationScopesRequestValidator() =>
        RuleFor(request => request.ResourceGroupId)
            .MongoObjectId("Resource group ID");
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
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyList<IsolationScopeDto> scopes => Results.Ok(scopes),
                    ResourceGroupNotFound error => ResourceGroupHttpMappings.Map(error)
                });
            })
            .WithName("GetIsolationScopes")
            .WithSummary("Lists isolation scopes owned by a resource group.")
            .Produces<IReadOnlyList<IsolationScopeDto>>();

        return app;
    }
}
