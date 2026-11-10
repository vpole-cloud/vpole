using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Diagnostics;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceDeployments.Application.Endpoints;

public sealed record GetResourceDeploymentDiagnosticsRequest(
    [FromRoute] string DeploymentId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetResourceDeploymentDiagnosticsOutcome>, ITenantReadOperation;

public union GetResourceDeploymentDiagnosticsOutcome(ResourceDeploymentDiagnosticsDto, ResourceDeploymentNotFound);

public sealed class GetResourceDeploymentDiagnosticsValidator
    : AbstractValidator<GetResourceDeploymentDiagnosticsRequest>
{
    public GetResourceDeploymentDiagnosticsValidator()
    {
        RuleFor(request => request.DeploymentId).MongoObjectId("Deployment ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
    }
}

public sealed class GetResourceDeploymentDiagnosticsHandler(
    ResourceDeploymentOperationsPersistence persistence,
    ResourceDeploymentDiagnostics diagnostics
) : IMediatorOutcomeHandler<GetResourceDeploymentDiagnosticsRequest, GetResourceDeploymentDiagnosticsOutcome>
{
    public async Task<GetResourceDeploymentDiagnosticsOutcome> HandleAsync(
        GetResourceDeploymentDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        var deployment = await persistence.LoadAsync(
            request.TenantId,
            request.DeploymentId.ToLowerInvariant(),
            cancellationToken);
        if (deployment is null)
            return new ResourceDeploymentNotFound(request.DeploymentId);
        return await diagnostics.DiscoverAsync(deployment, cancellationToken);
    }
}

public static class GetResourceDeploymentDiagnosticsEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceDeploymentDiagnosticsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{DeploymentId}/diagnostics",
                async (
                    IMediator mediator, [AsParameters] GetResourceDeploymentDiagnosticsRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    ResourceDeploymentDiagnosticsDto value => Results.Ok(value),
                    ResourceDeploymentNotFound => Results.NotFound(),
                }))
            .WithName("GetResourceDeploymentDiagnostics");
        return app;
    }
}
