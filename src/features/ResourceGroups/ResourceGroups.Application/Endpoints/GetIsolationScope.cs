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

public sealed record GetIsolationScopeRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string ScopeId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetIsolationScopeOutcome>, ITenantReadOperation;

public union GetIsolationScopeOutcome(
    IsolationScopeDto,
    ResourceGroupNotFound,
    IsolationScopeNotFound);

public sealed class GetIsolationScopeHandler(
    LoadResourceGroup loadResourceGroup,
    LoadIsolationScope loadIsolationScope)
    : IMediatorOutcomeHandler<GetIsolationScopeRequest, GetIsolationScopeOutcome>
{
    public async Task<GetIsolationScopeOutcome> HandleAsync(
        GetIsolationScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await loadResourceGroup(
            request.TenantId,
            request.ResourceGroupId,
            cancellationToken);
        if (resourceGroup is null)
            return new ResourceGroupNotFound(request.ResourceGroupId);

        var scope = await loadIsolationScope(
            resourceGroup.TenantId,
            resourceGroup.Id,
            request.ScopeId,
            cancellationToken);
        return scope is null
            ? new IsolationScopeNotFound(request.ScopeId)
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
                IHttpProblemMapper httpProblems,
                [AsParameters] GetIsolationScopeRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IsolationScopeDto scope => Results.Ok(scope),
                    ResourceGroupNotFound error => httpProblems.Map(error).ToHttpResult(),
                    IsolationScopeNotFound error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetIsolationScope")
            .WithSummary("Gets an isolation scope owned by a resource group.")
            .Produces<IsolationScopeDto>();

        return app;
    }
}
