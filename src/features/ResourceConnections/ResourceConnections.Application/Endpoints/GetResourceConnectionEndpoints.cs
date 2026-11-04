using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;
using static Pole.Features.ResourceConnections.Application.ResourceConnectionHttpProblemMappings;

namespace Pole.Features.ResourceConnections.Application.Endpoints;

public sealed record GetResourceConnectionEndpointsRequest(
    [FromQuery] string ResourceType,
    [FromQuery] string ResourceId,
    [FromTenantHeader] string TenantId,
    [FromQuery] string? SourceResourceType = null,
    [FromQuery] string? SourceResourceId = null)
    : IMediatorRequest<GetResourceConnectionEndpointsOutcome>, ITenantReadOperation;

public sealed record ConnectionEndpointMetadata(
    string Id,
    string DisplayName,
    string Protocol,
    string Transport,
    int Port,
    IReadOnlyList<ResourceConnectionConfigurationField> Configuration);
public sealed record ConnectionEndpointTopology(
    string Status,
    string? ReasonCode = null);
public sealed record ResourceConnectionEndpointsDto(
    string ResourceType,
    string ResourceId,
    string DisplayName,
    string Availability,
    IReadOnlyList<ConnectionEndpointMetadata> Endpoints,
    ConnectionEndpointTopology Topology);
public union GetResourceConnectionEndpointsOutcome(
    ResourceConnectionEndpointsDto,
    ConnectionResourceMissing);

public sealed class GetResourceConnectionEndpointsValidator : AbstractValidator<GetResourceConnectionEndpointsRequest>
{
    public GetResourceConnectionEndpointsValidator()
    {
        RuleFor(request => request.ResourceType).Must(value => ResourceTypeId.TryCreate(value, out _));
        RuleFor(request => request.ResourceId).MongoObjectId("Resource ID");
        When(request => request.SourceResourceType is not null || request.SourceResourceId is not null, () =>
        {
            RuleFor(request => request.SourceResourceType).NotEmpty().Must(value => ResourceTypeId.TryCreate(value, out _));
            RuleFor(request => request.SourceResourceId).NotEmpty().MongoObjectId("Source resource ID");
        });
    }
}

public sealed class GetResourceConnectionEndpointsHandler(LoadRegisteredResource loadResource,
    ResourceConnectionEndpointCatalog catalog, LoadResourceDeploymentSet loadPlacements)
    : IMediatorOutcomeHandler<GetResourceConnectionEndpointsRequest, GetResourceConnectionEndpointsOutcome>
{
    public async Task<GetResourceConnectionEndpointsOutcome> HandleAsync(GetResourceConnectionEndpointsRequest request, CancellationToken cancellationToken = default)
    {
        var type = ResourceTypeId.Create(request.ResourceType);
        var id = ObjectId.Parse(request.ResourceId).ToString();
        var resource = await loadResource(request.TenantId, type, id, cancellationToken);
        if (resource is null) return new ConnectionResourceMissing();
        ResourceTypeId? sourceType = null;
        string? sourceId = null;
        if (request.SourceResourceType is not null)
        {
            sourceType = ResourceTypeId.Create(request.SourceResourceType);
            sourceId = ObjectId.Parse(request.SourceResourceId!).ToString();
            if (await loadResource(request.TenantId, sourceType.Value, sourceId, cancellationToken) is null) return new ConnectionResourceMissing();
        }
        var supported = catalog.SupportsDestination(type);
        var published = supported ? await catalog.LoadAvailableAsync(request.TenantId, type, id, cancellationToken) : null;
        if (supported && published is null) return new ConnectionResourceMissing();
        var topology = new ConnectionEndpointTopology("NotEvaluated");
        if (sourceType is not null)
        {
            if (!catalog.SupportsSource(sourceType.Value)) topology = new("Ineligible", "SourceUnsupported");
            else if (!supported) topology = new("Ineligible", "DestinationUnsupported");
            else
            {
                var source = await loadPlacements(request.TenantId, sourceType.Value, sourceId!, cancellationToken);
                var destination = await loadPlacements(request.TenantId, type, id, cancellationToken);
                var assessment = ResourceConnectionTopology.Evaluate(DesiredTargets(source), DesiredTargets(destination));
                topology = assessment.Kind == ConnectionTopologyKind.Eligible ? new("Eligible") : new("Ineligible", assessment.Kind.ToString());
            }
        }
        return new ResourceConnectionEndpointsDto(type.Value, id, resource.DisplayName, supported ? "Supported" : "Unsupported",
            published?.Endpoints.Select(endpoint => new ConnectionEndpointMetadata(endpoint.Id, endpoint.DisplayName, endpoint.Protocol,
                endpoint.Transport, endpoint.ServicePort.Value, endpoint.Configuration)).ToArray() ?? [], topology);
    }

    private static IEnumerable<string> DesiredTargets(IEnumerable<PlacementSummaryDto> placements) => placements
        .Where(item => item.DesiredOperation != ResourceDeploymentDesiredOperation.Delete && item.Status != ResourceDeploymentStatus.Deleted).Select(item => item.DeploymentTargetId);
}

public static class GetResourceConnectionEndpointsEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceConnectionEndpointsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/endpoints", async (IMediator mediator, [AsParameters] GetResourceConnectionEndpointsRequest request, CancellationToken cancellationToken) =>
            (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
            {
                ResourceConnectionEndpointsDto response => Results.Ok(response),
                ConnectionResourceMissing missing => MapResourceMissing(missing)
            })).WithName("GetResourceConnectionEndpoints").Produces<ResourceConnectionEndpointsDto>();
        return app;
    }
}
