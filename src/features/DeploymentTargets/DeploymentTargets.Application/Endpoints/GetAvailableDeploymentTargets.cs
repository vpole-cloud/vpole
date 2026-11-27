// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

/// <summary>
///     Tenant-visible placement discovery. This endpoint deliberately returns only display and
///     ingress-capability information; platform target administration remains under /platform.
/// </summary>
public sealed record GetAvailableDeploymentTargetsRequest(
    [FromQuery] string ResourceType,
    [FromQuery] bool RequiresPublicIngress,
    [FromQuery] string? IsolationScopeId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetAvailableDeploymentTargetsOutcome>, ITenantReadOperation;

public union GetAvailableDeploymentTargetsOutcome(IReadOnlyCollection<AvailableDeploymentTargetDto>);

public sealed class GetAvailableDeploymentTargetsHandler(
    LoadEligibleDeploymentTargets loadEligibleDeploymentTargets
) : IMediatorOutcomeHandler<GetAvailableDeploymentTargetsRequest, GetAvailableDeploymentTargetsOutcome>
{
    public async Task<GetAvailableDeploymentTargetsOutcome> HandleAsync(
        GetAvailableDeploymentTargetsRequest request,
        CancellationToken cancellationToken = default)
    {
        var targets = await loadEligibleDeploymentTargets(
            request.RequiresPublicIngress
                ? DeploymentTargetRequiredCapabilities.PublicIngress
                : DeploymentTargetRequiredCapabilities.None,
            cancellationToken);
        return targets.Select(target => target.ToAvailableDto()).ToArray();
    }
}

public sealed class GetAvailableDeploymentTargetsRequestValidator
    : AbstractValidator<GetAvailableDeploymentTargetsRequest>
{
    public GetAvailableDeploymentTargetsRequestValidator()
    {
        RuleFor(request => request.ResourceType).ResourceTypeId("Resource type");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
        When(
            request => request.IsolationScopeId is not null,
            () => RuleFor(request => request.IsolationScopeId!).MongoObjectId("Isolation scope ID"));
    }
}

public static class GetAvailableDeploymentTargetsEndpoint
{
    public static IEndpointRouteBuilder MapGetAvailableDeploymentTargetsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/available",
                async (
                    IMediator mediator, [AsParameters] GetAvailableDeploymentTargetsRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        IReadOnlyCollection<AvailableDeploymentTargetDto> targets => Results.Ok(targets),
                    });
                })
            .WithName("GetAvailableDeploymentTargets")
            .Produces<IReadOnlyCollection<AvailableDeploymentTargetDto>>();
        return app;
    }
}
