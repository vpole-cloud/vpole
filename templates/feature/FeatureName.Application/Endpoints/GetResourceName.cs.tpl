using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.FeatureName.Application.Common;
using Pole.Features.FeatureName.Application.Reads;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.FeatureName.Application.ResourceNameHttpProblems;

namespace Pole.Features.FeatureName.Application.Endpoints;

public static class GetResourceNameEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceNameEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/{Id}",
                async (
                    IMediator mediator, [AsParameters] GetResourceNameRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    ResourceNameDto value => Results.Ok(value),
                    ResourceNameNotFound error => MapMissing(error)
                }))
            .WithName("FeatureName.GetResourceName")
            .Produces<ResourceNameDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }
}

public sealed record GetResourceNameRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetResourceNameOutcome>, ITenantReadOperation;

public union GetResourceNameOutcome(ResourceNameDto, ResourceNameNotFound);

public sealed class GetResourceNameValidator : AbstractValidator<GetResourceNameRequest>
{
    public GetResourceNameValidator() => RuleFor(request => request.Id).Length(24).MongoObjectId("Resource ID");
}

public sealed class GetResourceNameHandler(
    LoadResourceName load
) : IMediatorOutcomeHandler<GetResourceNameRequest, GetResourceNameOutcome>
{
    public async Task<GetResourceNameOutcome> HandleAsync(
        GetResourceNameRequest request,
        CancellationToken cancellationToken = default)
    {
        var resource = await load(new(request.TenantId, request.Id), cancellationToken);
        return resource is null ? new ResourceNameNotFound(request.Id) : ResourceNameDto.From(resource);
    }
}
