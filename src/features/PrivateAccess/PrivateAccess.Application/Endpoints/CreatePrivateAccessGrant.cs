// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Domain;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

public sealed record CreatePrivateAccessGrantBody(
    string SubjectUserId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string EndpointId,
    DateTime ExpiresAtUtc
);

public sealed record CreatePrivateAccessGrantRequest(
    [FromBody] CreatePrivateAccessGrantBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue ActorUserId
) : IMediatorRequest<CreatePrivateAccessGrantOutcome>, ITenantCreateOperation;

public sealed record PrivateAccessGrantDto(
    string Id,
    string TenantId,
    string SubjectUserId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string EndpointId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    bool Revoked
);

public union CreatePrivateAccessGrantOutcome(
    PrivateAccessGrantDto,
    PrivateAccessManageRoleRequired,
    PrivateAccessSubjectNotMember,
    PrivateAccessResourceUnavailable,
    PrivateAccessEndpointUnsupported,
    PrivateAccessGrantExpiryInvalid);

public sealed class CreatePrivateAccessGrantHandler(
    PersistPrivateAccessGrant persist
) : IMediatorOutcomeHandler<CreatePrivateAccessGrantRequest, CreatePrivateAccessGrantOutcome>
{
    public async Task<CreatePrivateAccessGrantOutcome> HandleAsync(
        CreatePrivateAccessGrantRequest request,
        CancellationToken cancellationToken = default)
    {
        var body = request.Body;
        var persisted = await persist(
            new CreatePrivateAccessGrantCommand(
                request.TenantId,
                request.ActorUserId.Value,
                body.SubjectUserId,
                ResourceTypeId.Create(body.ResourceType),
                body.ResourceId,
                body.ResourceDeploymentId,
                body.EndpointId,
                body.ExpiresAtUtc),
            cancellationToken);
        return persisted switch
        {
            PrivateAccessGrant grant => grant.ToDto(),
            PrivateAccessManageRoleRequired error => error,
            PrivateAccessSubjectNotMember error => error,
            PrivateAccessResourceUnavailable error => error,
            PrivateAccessEndpointUnsupported error => error,
            PrivateAccessGrantExpiryInvalid error => error,
        };
    }
}

public sealed class CreatePrivateAccessGrantValidator : AbstractValidator<CreatePrivateAccessGrantRequest>
{
    public CreatePrivateAccessGrantValidator()
    {
        RuleFor(item => item.Body).NotNull();
        When(
            item => item.Body is not null,
            () =>
            {
                RuleFor(item => item.Body.SubjectUserId).NotEmpty().MaximumLength(120);
                RuleFor(item => item.Body.ResourceType).Must(value => ResourceTypeId.TryCreate(value, out _));
                RuleFor(item => item.Body.ResourceId).MongoObjectId("Resource ID");
                RuleFor(item => item.Body.ResourceDeploymentId).MongoObjectId("Deployment ID");
                RuleFor(item => item.Body.EndpointId).NotEmpty().MaximumLength(100);
                RuleFor(item => item.Body.ExpiresAtUtc).Must(value => value.Kind == DateTimeKind.Utc);
            });
    }
}

public static class CreatePrivateAccessGrantEndpoint
{
    public static IEndpointRouteBuilder MapCreatePrivateAccessGrantEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/",
                async (
                        IMediator mediator, [AsParameters] CreatePrivateAccessGrantRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        PrivateAccessGrantDto grant => Results.Created(
                            $"/api/v1/private-access/grants/{grant.Id}",
                            grant),
                        PrivateAccessManageRoleRequired error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessSubjectNotMember error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessResourceUnavailable error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessEndpointUnsupported error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessGrantExpiryInvalid error => PrivateAccessHttpProblemMappings.Map(error),
                    }))
            .WithName("CreatePrivateAccessGrant")
            .Produces<PrivateAccessGrantDto>(StatusCodes.Status201Created);
        return app;
    }
}
