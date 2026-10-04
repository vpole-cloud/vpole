using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Endpoints;

public sealed record GetResourceDeploymentOperationsRequest(
    [FromTenantHeader] string TenantId,
    [FromQuery] string? ResourceGroupId = null,
    [FromQuery] string? ResourceType = null,
    [FromQuery] string? ResourceId = null,
    [FromQuery] int PageNumber = 1,
    [FromQuery] int PageSize = 10) : IMediatorRequest<GetResourceDeploymentOperationsOutcome>, ITenantReadOperation;

public union GetResourceDeploymentOperationsOutcome(ResourceDeploymentOperationsPage);

public sealed class GetResourceDeploymentOperationsValidator : AbstractValidator<GetResourceDeploymentOperationsRequest>
{
    public GetResourceDeploymentOperationsValidator()
    {
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
        RuleFor(request => request.ResourceGroupId).MongoObjectId("Resource group ID").When(request => request.ResourceGroupId is not null);
        RuleFor(request => request.ResourceId).MongoObjectId("Resource ID").When(request => request.ResourceId is not null);
        RuleFor(request => request.ResourceType).Must(value => value is not null && ResourceTypeId.TryCreate(value, out _)).When(request => request.ResourceType is not null);
        RuleFor(request => request.ResourceType).NotNull().When(request => request.ResourceId is not null);
        RuleFor(request => request.ResourceId).NotNull().When(request => request.ResourceType is not null);
        RuleFor(request => request.PageNumber).InclusiveBetween(1, 10000);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class GetResourceDeploymentOperationsHandler(ResourceDeploymentOperationsPersistence persistence)
    : IMediatorOutcomeHandler<GetResourceDeploymentOperationsRequest, GetResourceDeploymentOperationsOutcome>
{
    public async Task<GetResourceDeploymentOperationsOutcome> HandleAsync(GetResourceDeploymentOperationsRequest request, CancellationToken cancellationToken = default) =>
        await persistence.PageAsync(request.TenantId, request.ResourceGroupId?.ToLowerInvariant(), request.ResourceType, request.ResourceId?.ToLowerInvariant(),
            request.PageNumber, request.PageSize, cancellationToken);
}

public static class GetResourceDeploymentOperationsEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceDeploymentOperationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/operations", async (IMediator mediator, [AsParameters] GetResourceDeploymentOperationsRequest request, CancellationToken cancellationToken) =>
            (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
            {
                ResourceDeploymentOperationsPage page => Results.Ok(page)
            })).WithName("GetResourceDeploymentOperations");
        return app;
    }
}
