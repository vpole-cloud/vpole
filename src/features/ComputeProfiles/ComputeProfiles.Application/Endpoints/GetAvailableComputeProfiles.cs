using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Persistence.Reads;
using Pole.Features.Core.Multitenancy;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ComputeProfiles.Application.Endpoints;

public sealed record GetAvailableComputeProfilesRequest(
    [FromQuery] string DeploymentTargetId,
    [FromQuery] string ResourceType,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetAvailableComputeProfilesOutcome>,
        ITenantReadOperation;

public union GetAvailableComputeProfilesOutcome(
    IReadOnlyCollection<AvailableComputeProfileDto>);

public sealed class GetAvailableComputeProfilesHandler(
    LoadAvailableComputeProfiles loadAvailableComputeProfiles)
    : IMediatorOutcomeHandler<
        GetAvailableComputeProfilesRequest,
        GetAvailableComputeProfilesOutcome>
{
    public async Task<GetAvailableComputeProfilesOutcome> HandleAsync(
        GetAvailableComputeProfilesRequest request,
        CancellationToken cancellationToken = default)
    {
        var profiles = await loadAvailableComputeProfiles(
            new ComputeProfileAvailabilityQuery(
                request.TenantId,
                request.DeploymentTargetId,
                ResourceTypeId.Create(request.ResourceType)),
            cancellationToken);
        return profiles.Select(profile => profile.ToAvailableDto()).ToArray();
    }
}

public sealed class GetAvailableComputeProfilesRequestValidator
    : AbstractValidator<GetAvailableComputeProfilesRequest>
{
    public GetAvailableComputeProfilesRequestValidator()
    {
        RuleFor(request => request.DeploymentTargetId)
            .MongoObjectId("Deployment target ID");
        RuleFor(request => request.ResourceType)
            .Must(ComputeProfileRequestParsing.IsResourceType)
            .WithMessage("Resource type is invalid.");
        RuleFor(request => request.TenantId)
            .MongoObjectId(TenantHeaders.TenantId);
    }
}

public static class GetAvailableComputeProfilesEndpoint
{
    public static IEndpointRouteBuilder MapGetAvailableComputeProfilesEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/available", async (
                IMediator mediator,
                [AsParameters] GetAvailableComputeProfilesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyCollection<AvailableComputeProfileDto> profiles => Results.Ok(profiles)
                });
            })
            .WithName("GetAvailableComputeProfiles")
            .Produces<IReadOnlyCollection<AvailableComputeProfileDto>>();
        return app;
    }
}
