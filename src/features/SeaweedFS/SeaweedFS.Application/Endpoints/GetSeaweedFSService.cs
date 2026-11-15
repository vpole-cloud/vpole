using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.SeaweedFS.Application.Endpoints;

using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SeaweedFS.Domain;

public union GetSeaweedFSServiceOutcome(SeaweedFSServiceDto, SeaweedFSNotFound);

public sealed record GetSeaweedFSServiceRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetSeaweedFSServiceOutcome>, ITenantReadOperation;

public sealed class GetSeaweedFSServiceValidator : AbstractValidator<GetSeaweedFSServiceRequest>
{
    public GetSeaweedFSServiceValidator() => RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
}

public sealed class GetSeaweedFSServiceHandler(
    LoadSeaweedFSById load,
    LoadResourceDeployment placement
) : IMediatorOutcomeHandler<GetSeaweedFSServiceRequest, GetSeaweedFSServiceOutcome>
{
    public async Task<GetSeaweedFSServiceOutcome> HandleAsync(
        GetSeaweedFSServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var service = await load(request.TenantId, request.Id, cancellationToken);
        if (service is null || service.Lifecycle is DeletedSeaweedFSLifecycle)
            return new SeaweedFSNotFound();
        return service.ToDto(
            await placement(
                new(request.TenantId, SeaweedFSResourceDeployment.ResourceType, request.Id),
                cancellationToken));
    }
}

public static class GetSeaweedFSServiceEndpoint
{
    public static IEndpointRouteBuilder MapGetSeaweedFSServiceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}",
                async (
                    IMediator mediator, [AsParameters] GetSeaweedFSServiceRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SeaweedFSServiceDto dto => Results.Ok(dto),
                    SeaweedFSNotFound => SeaweedFSHttpMappings.Problem(404, "not-found", "SeaweedFS resource not found")
                }))
            .WithName("GetSeaweedFSService");
        return app;
    }
}
