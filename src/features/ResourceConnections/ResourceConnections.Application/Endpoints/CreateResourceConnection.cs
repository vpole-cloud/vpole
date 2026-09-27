using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.RedisServices.Contracts;
using Pole.Mediator;
using Pole.Validation;
using Pole.Workloads;
using static Pole.Features.ResourceConnections.Application.ResourceConnectionHttpProblemMappings;

namespace Pole.Features.ResourceConnections.Application.Endpoints;

public sealed record CreateResourceConnectionBody(
    string Name,
    string SourceResourceType,
    string SourceResourceId,
    string DestinationResourceType,
    string DestinationResourceId,
    string EndpointId);

public sealed record CreateResourceConnectionRequest(
    [FromBody] CreateResourceConnectionBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<CreateResourceConnectionOutcome>, ITenantCreateOperation;

public sealed record CreateResourceConnectionResponse(
    string Id,
    string Name,
    string EndpointId,
    long Generation,
    string Status,
    IReadOnlyList<string> TargetIds);

public union CreateResourceConnectionOutcome(
    CreateResourceConnectionResponse,
    ConnectionSourceUnsupported,
    ConnectionResourceMissing,
    ConnectionEndpointUnsupported,
    ConnectionSourceHasNoPlacements,
    ConnectionPlacementDeleting,
    ConnectionDestinationTargetMissing,
    ConnectionDestinationTargetAmbiguous,
    ConnectionNameAlreadyExists);

public sealed record ConnectionSourceUnsupported;

public sealed class CreateResourceConnectionHandler(
    PersistResourceConnectionCreation persist)
    : IMediatorOutcomeHandler<CreateResourceConnectionRequest, CreateResourceConnectionOutcome>
{
    public async Task<CreateResourceConnectionOutcome> HandleAsync(
        CreateResourceConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var body = request.Body;
        var destinationType = ResourceTypeId.Create(body.DestinationResourceType);
        if (!ResourceConnectionEndpointCatalog.SupportsDestination(destinationType))
        {
            return new ConnectionEndpointUnsupported();
        }
        if (destinationType == RedisConnectionEndpoints.ResourceType &&
            RedisConnectionEndpoints.Resolve(body.EndpointId) is null)
            return new ConnectionEndpointUnsupported();
        var sourceType = ResourceTypeId.Create(body.SourceResourceType);
        if (!ResourceConnectionSourceTypes.Contains(sourceType))
            return new ConnectionSourceUnsupported();

        var outcome = await persist(new CreateResourceConnectionCommand(
            request.TenantId,
            body.Name,
            sourceType,
            body.SourceResourceId,
            destinationType,
            body.DestinationResourceId,
            body.EndpointId), cancellationToken);
        return outcome switch
        {
            ResourceConnection connection => new CreateResourceConnectionResponse(
                connection.Id,
                connection.Name,
                connection.EndpointId,
                connection.Generation,
                "Pending",
                ((ActiveResourceConnection)connection.Lifecycle).Targets
                    .Select(target => target.Snapshot.TargetId)
                    .ToArray()),
            ConnectionResourceMissing error => error,
            ConnectionEndpointUnsupported error => error,
            ConnectionSourceHasNoPlacements error => error,
            ConnectionPlacementDeleting error => error,
            ConnectionDestinationTargetMissing error => error,
            ConnectionDestinationTargetAmbiguous error => error,
            ConnectionNameAlreadyExists error => error
        };
    }
}

public sealed class CreateResourceConnectionValidator : AbstractValidator<CreateResourceConnectionRequest>
{
    public CreateResourceConnectionValidator()
    {
        RuleFor(request => request.Body).NotNull();
        When(request => request.Body is not null, () =>
        {
            RuleFor(request => request.Body.Name)
                .NotEmpty().MaximumLength(30).Matches("^[a-z0-9-]+$");
            RuleFor(request => request.Body.SourceResourceType)
                .Must(value => ResourceTypeId.TryCreate(value, out _));
            RuleFor(request => request.Body.DestinationResourceType)
                .Must(value => ResourceTypeId.TryCreate(value, out _));
            RuleFor(request => request.Body.SourceResourceId)
                .MongoObjectId("Source resource ID");
            RuleFor(request => request.Body.DestinationResourceId)
                .MongoObjectId("Destination resource ID");
            RuleFor(request => request.Body.EndpointId).NotEmpty().MaximumLength(100);
        });
    }
}

public static class CreateResourceConnectionEndpoint
{
    public static IEndpointRouteBuilder MapCreateResourceConnectionEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                IMediator mediator,
                [AsParameters] CreateResourceConnectionRequest request,
                CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken))
                .ToHttpResult(outcome => outcome switch
                {
                    CreateResourceConnectionResponse response => Results.Accepted(
                        $"/api/v1/resource-connections/{response.Id}", response),
                    ConnectionResourceMissing error => MapResourceMissing(error),
                    ConnectionEndpointUnsupported error => MapEndpointUnsupported(error),
                    ConnectionSourceUnsupported error => MapSourceUnsupported(error),
                    ConnectionSourceHasNoPlacements error => MapNoSourcePlacement(error),
                    ConnectionPlacementDeleting error => MapPlacementDeleting(error),
                    ConnectionDestinationTargetMissing error => MapDestinationTargetMissing(error),
                    ConnectionDestinationTargetAmbiguous error => MapDestinationTargetAmbiguous(error),
                    ConnectionNameAlreadyExists error => MapNameAlreadyExists(error)
                }))
            .WithName("CreateResourceConnection")
            .Produces<CreateResourceConnectionResponse>(StatusCodes.Status202Accepted);

        return app;
    }
}
