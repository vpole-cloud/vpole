// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Application.ResourceRegistrations;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class GetResourceGroup
{
    public static IEndpointRouteBuilder MapGetResourceGroupEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}",
                async (
                    IMediator mediator, [AsParameters] GetResourceGroupRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        ResourceGroupDto group => Results.Ok(group),
                        ResourceGroupNotFound error => ResourceGroupHttpMappings.Map(error),
                    });
                })
            .WithName("GetResourceGroup")
            .WithSummary("Retrieves a resource group in the selected tenant.")
            .Produces<ResourceGroupDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}

public sealed record GetResourceGroupRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetResourceGroupOutcome>, ITenantReadOperation;

public union GetResourceGroupOutcome(ResourceGroupDto, ResourceGroupNotFound);

public sealed class GetResourceGroupHandler(
    LoadResourceGroup loadResourceGroup,
    LoadResourceRegistrationCount loadResourceRegistrationCount,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetResourceGroupRequest, GetResourceGroupOutcome>
{
    public async Task<GetResourceGroupOutcome> HandleAsync(
        GetResourceGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await loadResourceGroup(request.TenantId, request.Id, cancellationToken);
        if (group is null)
            return new ResourceGroupNotFound(request.Id);
        return group.ToDto(await loadResourceRegistrationCount(request.TenantId, request.Id, cancellationToken)) with
        {
            GitOps = await git.StatusAsync(
                new LoadGitResourceStatusRequest(
                    TenantId: request.TenantId,
                    Kind: "ResourceGroup",
                    ResourceId: group.Id),
                ct: cancellationToken),
        };
    }
}

public sealed class GetResourceGroupRequestValidator : AbstractValidator<GetResourceGroupRequest>
{
    public GetResourceGroupRequestValidator() => RuleFor(request => request.Id).MongoObjectId("Resource group ID");
}
