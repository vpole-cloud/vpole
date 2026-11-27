// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.FeatureName.Application.Common;
using Pole.Features.FeatureName.Application.Creation;
using Pole.Features.FeatureName.Domain;
using Pole.Mediator;
using static Pole.Features.FeatureName.Application.ResourceNameHttpProblems;

namespace Pole.Features.FeatureName.Application.Endpoints;

public static class CreateResourceNameEndpoint
{
    public static IEndpointRouteBuilder MapCreateResourceNameEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/",
                async (
                    IMediator mediator, [AsParameters] CreateResourceNameRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    ResourceNameDto value => Results.Created($"/api/v1/feature-route/{value.Id}", value),
                    ResourceNameNameAlreadyExists error => MapNameConflict(error)
                }))
            .WithName("FeatureName.CreateResourceName")
            .Produces<ResourceNameDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }
}

public sealed record CreateResourceNameRequest(
    [FromBody] ResourceNameDetailsDto Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<CreateResourceNameOutcome>, ITenantCreateOperation;

public union CreateResourceNameOutcome(ResourceNameDto, ResourceNameNameAlreadyExists);

public sealed class CreateResourceNameValidator : AbstractValidator<CreateResourceNameRequest>
{
    public CreateResourceNameValidator() => RuleFor(request => request.Body)
        .NotNull()
        .SetValidator(new ResourceNameDetailsValidator());
}

public sealed class CreateResourceNameHandler(
    PersistCreatedResourceName persist,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<CreateResourceNameRequest, CreateResourceNameOutcome>
{
    public async Task<CreateResourceNameOutcome> HandleAsync(
        CreateResourceNameRequest request,
        CancellationToken cancellationToken = default)
    {
        var details = ResourceNameDetails.Create(request.Body.Name, request.Body.Description);
        var resource = ResourceNameFactory.Create(
            request.TenantId,
            details,
            new(ObjectId.GenerateNewId().ToString(), timeProvider.GetUtcNow().UtcDateTime));
        var result = await persist(resource, cancellationToken);
        return result switch
        {
            ResourceNamePersisted => ResourceNameDto.From(resource),
            ResourceNameNameAlreadyExists error => error
        };
    }
}
