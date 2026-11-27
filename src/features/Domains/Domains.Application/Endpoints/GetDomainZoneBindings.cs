// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;
using static Pole.Features.Domains.Application.Common.DomainHttpMappings;

namespace Pole.Features.Domains.Application.Endpoints;

public static class GetDomainZoneBindings
{
    public static IEndpointRouteBuilder MapGetDomainZoneBindingsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/bindings",
                async (
                        IMediator mediator, [AsParameters] GetDomainZoneBindingsRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        GetDomainZoneBindingsResponse response => Results.Ok(response),
                        DomainZoneNotFound error => MapDomainZoneNotFound(error),
                    }))
            .WithName("GetDomainZoneBindings")
            .Produces<GetDomainZoneBindingsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}

public sealed record GetDomainZoneBindingsRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromQuery] int PageNumber = 1,
    [FromQuery] int PageSize = 50
) : IMediatorRequest<GetDomainZoneBindingsOutcome>, ITenantReadOperation;

public sealed record DomainZoneBindingUsage(
    HostnameBindingSnapshot Binding,
    string? ResourceDisplayName,
    string? ResourceGroupId,
    PlacementSummaryDto? Placement,
    GitResourceStatus? GitOps
);

public sealed record GetDomainZoneBindingsResponse(
    IReadOnlyList<DomainZoneBindingUsage> Items,
    long TotalCount,
    int PageNumber,
    int PageSize
);

public union GetDomainZoneBindingsOutcome(GetDomainZoneBindingsResponse, DomainZoneNotFound);

public sealed class GetDomainZoneBindingsValidator : AbstractValidator<GetDomainZoneBindingsRequest>
{
    public GetDomainZoneBindingsValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Domain zone ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
        RuleFor(request => request.PageNumber).InclusiveBetween(1, 10000);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class GetDomainZoneBindingsHandler(
    LoadTenantDomainZone loadZone,
    LoadDomainZoneBindingPage loadBindings,
    LoadRegisteredResource loadResource,
    LoadExactResourceDeployment loadDeployment,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetDomainZoneBindingsRequest, GetDomainZoneBindingsOutcome>
{
    public async Task<GetDomainZoneBindingsOutcome> HandleAsync(
        GetDomainZoneBindingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var zone = await loadZone(request.TenantId, request.Id, cancellationToken);
        if (zone is DomainZoneNotFound missing)
            return missing;

        var page = await loadBindings(
            request.TenantId,
            request.Id,
            request.PageNumber,
            request.PageSize,
            cancellationToken);
        var statuses = await git.StatusesAsync(
            new LoadGitResourceStatusesRequest(
                request.TenantId,
                "HostnameBinding",
                page.Items.Select(item => item.Id).ToArray()),
            cancellationToken);
        var items = new List<DomainZoneBindingUsage>();
        var owners = new Dictionary<(string Type, string Id), RegisteredResource?>();
        foreach (var binding in page.Items)
        {
            var resourceType = ResourceTypeId.Create(binding.ResourceType);
            var key = (binding.ResourceType, binding.ResourceId);
            if (!owners.TryGetValue(key, out var owner))
            {
                owner = await loadResource(
                    new LoadRegisteredResourceRequest(
                        TenantId: request.TenantId,
                        ResourceTypeId: resourceType,
                        ResourceId: binding.ResourceId),
                    cancellationToken: cancellationToken);
                owners.Add(key, owner);
            }

            var deployment = owner is null
                ? null
                : await loadDeployment(
                    new LoadExactResourceDeploymentRequest(
                        TenantId: request.TenantId,
                        ResourceType: resourceType,
                        ResourceId: binding.ResourceId,
                        DeploymentId: binding.ResourceDeploymentId),
                    cancellationToken: cancellationToken);

            if (deployment?.DeploymentTargetId != binding.DeploymentTargetId)
                deployment = null;

            items.Add(
                new DomainZoneBindingUsage(
                    binding,
                    owner?.DisplayName,
                    owner?.ResourceGroupId,
                    deployment,
                    statuses.GetValueOrDefault(binding.Id)));
        }

        return new GetDomainZoneBindingsResponse(items, page.TotalCount, request.PageNumber, request.PageSize);
    }
}
