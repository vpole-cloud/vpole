// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.ResourceConnections.Application.ResourceConnectionHttpProblemMappings;

namespace Pole.Features.ResourceConnections.Application.Endpoints;

public sealed record DeleteResourceConnectionRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DeleteResourceConnectionOutcome>, ITenantWriteOperation;

public sealed record DeleteResourceConnectionResponse(
    string Id,
    long Generation,
    string Status
);

public union DeleteResourceConnectionOutcome(
    DeleteResourceConnectionResponse,
    ResourceConnectionNotFound,
    ResourceConnectionDeletionConflict,
    ResourceConnectionGitOwned);

public sealed class DeleteResourceConnectionRequestValidator : AbstractValidator<DeleteResourceConnectionRequest>
{
    public DeleteResourceConnectionRequestValidator() => RuleFor(request => request.Id).MongoObjectId("Connection ID");
}

public sealed class DeleteResourceConnectionHandler(
    BeginResourceConnectionDeletion beginDeletion
) : IMediatorOutcomeHandler<DeleteResourceConnectionRequest, DeleteResourceConnectionOutcome>
{
    public async Task<DeleteResourceConnectionOutcome> HandleAsync(
        DeleteResourceConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await beginDeletion(request.TenantId, request.Id, cancellationToken);
        return result switch
        {
            ResourceConnection connection => new DeleteResourceConnectionResponse(
                connection.Id,
                connection.Generation,
                "Deleting"),
            ResourceConnectionMissing => new ResourceConnectionNotFound(request.Id),
            ResourceConnectionDeletionConflict conflict => conflict,
            ResourceConnectionGitOwned conflict => conflict,
        };
    }
}

public static class DeleteResourceConnectionEndpoint
{
    public static IEndpointRouteBuilder MapDeleteResourceConnectionEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{Id}",
                async (
                        IMediator mediator, [AsParameters] DeleteResourceConnectionRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        DeleteResourceConnectionResponse response => Results.Accepted(value: response),
                        ResourceConnectionNotFound error => MapNotFound(error),
                        ResourceConnectionDeletionConflict error => MapDeletionConflict(error),
                        ResourceConnectionGitOwned error => MapGitOwned(error),
                    }))
            .WithName("DeleteResourceConnection")
            .Produces<DeleteResourceConnectionResponse>(StatusCodes.Status202Accepted);
        return app;
    }
}
