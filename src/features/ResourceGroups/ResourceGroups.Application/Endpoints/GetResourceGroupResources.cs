using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Request;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class GetResourceGroupResources
{
    public static IEndpointRouteBuilder MapGetResourceGroupResourcesEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ResourceGroupId}/resources", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetResourceGroupResourcesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    PaginatedResponse<ResourceRegistrationDto> page => Results.Ok(page),
                    ResourceGroupNotFound error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetResourceGroupResources")
            .WithSummary("Retrieves resources registered in a resource group.")
            .Produces<PaginatedResponse<ResourceRegistrationDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}

public sealed record GetResourceGroupResourcesRequest(
    [FromRoute] string ResourceGroupId,
    [FromTenantHeader] string TenantId,
    [FromQuery, DefaultValue(1)] int PageNumber = 1,
    [FromQuery, DefaultValue(20)] int PageSize = 20)
    : IMediatorRequest<GetResourceGroupResourcesOutcome>, ITenantReadOperation;

public union GetResourceGroupResourcesOutcome(
    PaginatedResponse<ResourceRegistrationDto>,
    ResourceGroupNotFound);

public sealed class GetResourceGroupResourcesHandler(
    LoadResourceGroup loadResourceGroup,
    LoadResourceRegistrationPage loadResourceRegistrationPage)
    : IMediatorOutcomeHandler<GetResourceGroupResourcesRequest, GetResourceGroupResourcesOutcome>
{
    public async Task<GetResourceGroupResourcesOutcome> HandleAsync(
        GetResourceGroupResourcesRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await loadResourceGroup(
            request.TenantId,
            request.ResourceGroupId,
            cancellationToken);
        if (group is null)
            return new ResourceGroupNotFound(request.ResourceGroupId);

        var page = await loadResourceRegistrationPage(
            new ResourceRegistrationPageQuery(
                request.TenantId,
                request.ResourceGroupId,
                request.PageNumber,
                request.PageSize),
            cancellationToken);
        return new PaginatedResponse<ResourceRegistrationDto>(
            page.Items.Select(item => item.ToDto()),
            page.TotalCount,
            request.PageNumber,
            request.PageSize);
    }
}

public sealed class GetResourceGroupResourcesRequestValidator
    : AbstractValidator<GetResourceGroupResourcesRequest>
{
    public GetResourceGroupResourcesRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId).MongoObjectId("Resource group ID");
        RuleFor(request => request.PageNumber).GreaterThan(0);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 200);
    }
}
