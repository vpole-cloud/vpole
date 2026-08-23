using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

/// <summary>
/// Tenant-visible placement discovery. This endpoint deliberately returns only display and
/// ingress-capability information; platform target administration remains under /platform.
/// </summary>
public sealed record GetAvailableDeploymentTargetsRequest(
    [FromQuery] string ResourceType,
    [FromQuery] bool RequiresPublicIngress,
    [FromQuery] string? IsolationScopeId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyCollection<AvailableDeploymentTargetDto>>,
        ITenantReadOperation;

public sealed class GetAvailableDeploymentTargetsHandler(
    LoadEligibleDeploymentTargets loadEligibleDeploymentTargets)
    : IMediatorOutcomeHandler<
        GetAvailableDeploymentTargetsRequest,
        IReadOnlyCollection<AvailableDeploymentTargetDto>>
{
    public async Task<IReadOnlyCollection<AvailableDeploymentTargetDto>> HandleAsync(
        GetAvailableDeploymentTargetsRequest request,
        CancellationToken cancellationToken = default)
    {
        var targets = await loadEligibleDeploymentTargets(
            request.RequiresPublicIngress
                ? DeploymentTargetRequiredCapabilities.PublicIngress
                : DeploymentTargetRequiredCapabilities.None,
            cancellationToken);
        return targets
            .Select(target => target.ToAvailableDto())
            .ToArray();
    }
}

public sealed class GetAvailableDeploymentTargetsRequestValidator
    : AbstractValidator<GetAvailableDeploymentTargetsRequest>
{
    public GetAvailableDeploymentTargetsRequestValidator()
    {
        RuleFor(request => request.ResourceType)
            .ResourceTypeId("Resource type");
        RuleFor(request => request.TenantId)
            .MongoObjectId(TenantHeaders.TenantId);
        When(
            request => request.IsolationScopeId is not null,
            () => RuleFor(request => request.IsolationScopeId!)
                .MongoObjectId("Isolation scope ID"));
    }
}

public static class GetAvailableDeploymentTargetsEndpoint
{
    public static IEndpointRouteBuilder MapGetAvailableDeploymentTargetsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/available", async (
                IMediator mediator,
                [AsParameters] GetAvailableDeploymentTargetsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetAvailableDeploymentTargets")
            .Produces<IReadOnlyCollection<AvailableDeploymentTargetDto>>();
        return app;
    }
}
