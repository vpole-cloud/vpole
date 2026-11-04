using FluentValidation;
using Pole.Features.GitOps.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.ResourceConnections.Application.ResourceConnectionHttpProblemMappings;

namespace Pole.Features.ResourceConnections.Application.Endpoints;

public sealed record GetResourceConnectionRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetResourceConnectionOutcome>, ITenantReadOperation;

public sealed record ResourceConnectionNotFound(string Id);

public union GetResourceConnectionOutcome(
    ResourceConnectionDto,
    ResourceConnectionNotFound);

public sealed class GetResourceConnectionRequestValidator
    : AbstractValidator<GetResourceConnectionRequest>
{
    public GetResourceConnectionRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Connection ID");
}

public sealed class GetResourceConnectionHandler(
    LoadResourceConnection loadConnection,
    IGitOpsMutations git)
    : IMediatorOutcomeHandler<GetResourceConnectionRequest, GetResourceConnectionOutcome>
{
    public async Task<GetResourceConnectionOutcome> HandleAsync(
        GetResourceConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = await loadConnection(request.TenantId, request.Id, cancellationToken);
        return connection is null
            ? new ResourceConnectionNotFound(request.Id)
            : connection.ToDto() with
            {
                GitOps = await git.StatusAsync(new(request.TenantId, "ResourceConnection", request.Id),
                    cancellationToken)
            };
    }
}

public static class GetResourceConnectionEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceConnectionEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (
                IMediator mediator,
                [AsParameters] GetResourceConnectionRequest request,
                CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken))
                .ToHttpResult(outcome => outcome switch
                {
                    ResourceConnectionDto response => Results.Ok(response),
                    ResourceConnectionNotFound error => MapNotFound(error)
                }))
            .WithName("GetResourceConnection")
            .Produces<ResourceConnectionDto>();
        return app;
    }
}
