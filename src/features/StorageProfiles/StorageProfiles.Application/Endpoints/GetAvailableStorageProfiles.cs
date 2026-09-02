using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Persistence;
using Pole.Features.StorageProfiles.Application.Persistence.Reads;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetAvailableStorageProfilesRequest(
    [FromQuery] string ResourceType,
    [FromQuery] string DeploymentTargetId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetAvailableStorageProfilesOutcome>, ITenantReadOperation;

public union GetAvailableStorageProfilesOutcome(
    IReadOnlyCollection<StorageProfileDto>,
    KubernetesStorageDiscoveryUnavailable);

public sealed class GetAvailableStorageProfilesHandler(
    DiscoverKubernetesStorageClassNames discoverStorageClassNames,
    LoadAvailableStorageProfiles loadAvailableStorageProfiles)
    : IMediatorOutcomeHandler<
        GetAvailableStorageProfilesRequest,
        GetAvailableStorageProfilesOutcome>
{
    public async Task<GetAvailableStorageProfilesOutcome> HandleAsync(
        GetAvailableStorageProfilesRequest request,
        CancellationToken cancellationToken = default)
    {
        var deploymentTargetId = request.DeploymentTargetId.Trim();
        var resourceType = ResourceTypeId.Create(request.ResourceType);
        var storageClassesResult = await discoverStorageClassNames(
            deploymentTargetId,
            cancellationToken);
        if (!storageClassesResult.TryGetValue(out var knownStorageClassNames, out var error))
        {
            return error;
        }

        var profiles = await loadAvailableStorageProfiles(
            new StorageProfileAvailabilityQuery(
                request.TenantId,
                deploymentTargetId,
                resourceType),
            cancellationToken);

        return profiles
            .Where(profile => profile.TargetMappings.Any(mapping =>
                string.Equals(
                    mapping.DeploymentTargetId,
                    deploymentTargetId,
                    StringComparison.Ordinal) &&
                knownStorageClassNames.Contains(mapping.StorageClassName)))
            .Select(profile => profile.ToDto())
            .ToArray();
    }
}

public sealed class GetAvailableStorageProfilesRequestValidator
    : AbstractValidator<GetAvailableStorageProfilesRequest>
{
    public GetAvailableStorageProfilesRequestValidator()
    {
        RuleFor(request => request.ResourceType)
            .Must(value => ResourceTypeId.TryCreate(value, out _))
            .WithMessage("Resource type is invalid.");
        RuleFor(request => request.DeploymentTargetId)
            .MongoObjectId("Deployment target ID");
        RuleFor(request => request.TenantId)
            .MongoObjectId(TenantHeaders.TenantId);
    }
}

public static class GetAvailableStorageProfilesEndpoint
{
    public static IEndpointRouteBuilder MapGetAvailableStorageProfilesEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                [AsParameters] GetAvailableStorageProfilesRequest request,
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyCollection<StorageProfileDto> profiles => Results.Ok(profiles),
                    KubernetesStorageDiscoveryUnavailable error =>
                        httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetAvailableStorageProfiles")
            .Produces<IReadOnlyCollection<StorageProfileDto>>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }
}
