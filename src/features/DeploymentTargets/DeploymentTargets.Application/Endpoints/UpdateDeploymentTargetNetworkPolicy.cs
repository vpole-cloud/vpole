// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record UpdateDeploymentTargetNetworkPolicyBody(
    NetworkPolicyEnforcement Enforcement,
    bool AllowIsolationNotEnforced,
    long Revision
);

public sealed record UpdateDeploymentTargetNetworkPolicyRequest(
    [FromRoute] string Id,
    [FromBody] UpdateDeploymentTargetNetworkPolicyBody Body
) : IMediatorRequest<UpdateDeploymentTargetNetworkPolicyOutcome>;

public union UpdateDeploymentTargetNetworkPolicyOutcome(
    DeploymentTargetDto,
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict);

public sealed class UpdateDeploymentTargetNetworkPolicyHandler(
    LoadDeploymentTargetById loadTarget,
    LoadDefaultDeploymentTargetId loadDefaultTargetId,
    PersistUpdatedDeploymentTarget persistTarget,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<UpdateDeploymentTargetNetworkPolicyRequest, UpdateDeploymentTargetNetworkPolicyOutcome>
{
    public async Task<UpdateDeploymentTargetNetworkPolicyOutcome> HandleAsync(
        UpdateDeploymentTargetNetworkPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await loadTarget(request.Id, cancellationToken);
        if (target is null)
            return new DeploymentTargetNotFound(request.Id);
        if (target.Revision != request.Body.Revision)
            return new DeploymentTargetRevisionConflict(request.Id);

        var now = DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc);
        var replacement = target.AttestNetworkPolicy(
            request.Body.Enforcement,
            request.Body.AllowIsolationNotEnforced,
            now);
        var result = await persistTarget(target, replacement, cancellationToken);
        return result switch
        {
            DeploymentTarget persisted => persisted.ToDto(persisted.Id == await loadDefaultTargetId(cancellationToken)),
            PersistUpdatedDeploymentTargetError error => error switch
            {
                DeploymentTargetRevisionConflict conflict => conflict,
                DeploymentTargetPublicBaseDomainReferenced or DeploymentTargetPublicBaseDomainConflict => throw
                    new InvalidOperationException("An unchanged ingress configuration cannot conflict."),
            },
        };
    }
}

public sealed class UpdateDeploymentTargetNetworkPolicyValidator
    : AbstractValidator<UpdateDeploymentTargetNetworkPolicyRequest>
{
    public UpdateDeploymentTargetNetworkPolicyValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
        RuleFor(request => request.Body).NotNull();
        When(
            request => request.Body is not null,
            () =>
            {
                RuleFor(request => request.Body.Enforcement).IsInEnum();
                RuleFor(request => request.Body.Revision).GreaterThan(0);
            });
    }
}

public static class UpdateDeploymentTargetNetworkPolicyEndpoint
{
    public static IEndpointRouteBuilder MapUpdateDeploymentTargetNetworkPolicyEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut(
                "/{Id}/network-policy",
                async (
                    IMediator mediator, [AsParameters] UpdateDeploymentTargetNetworkPolicyRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        DeploymentTargetDto dto => Results.Ok(dto),
                        DeploymentTargetNotFound error => DeploymentTargetHttpMappings.Map(error),
                        DeploymentTargetRevisionConflict error => DeploymentTargetHttpMappings.Map(error),
                    });
                })
            .WithName("UpdateDeploymentTargetNetworkPolicy")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
