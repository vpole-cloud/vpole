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

public union DeleteSeaweedFSServiceOutcome(
    SeaweedFSServiceDto,
    SeaweedFSNotFound,
    SeaweedFSConflict);

public sealed record DeleteSeaweedFSServiceRequest(
    [FromRoute] string Id,
    [FromQuery] long ExpectedRevision,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DeleteSeaweedFSServiceOutcome>, ITenantWriteOperation;

public sealed class DeleteSeaweedFSServiceValidator : AbstractValidator<DeleteSeaweedFSServiceRequest>
{
    public DeleteSeaweedFSServiceValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class DeleteSeaweedFSServiceHandler(
    SeaweedFSMutations mutations
) : IMediatorOutcomeHandler<DeleteSeaweedFSServiceRequest, DeleteSeaweedFSServiceOutcome>
{
    public async Task<DeleteSeaweedFSServiceOutcome> HandleAsync(
        DeleteSeaweedFSServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await mutations.DeleteAsync(
            request.TenantId,
            request.Id,
            request.ExpectedRevision,
            cancellationToken);
        return result switch
        {
            SeaweedFSServiceDto value => value,
            SeaweedFSNotFound value => value,
            SeaweedFSConflict value => value,
            SeaweedFSCredentialFailure => throw new InvalidOperationException(
                "Unexpected credential operation in resource mutation."),
            SeaweedFSBucketDto => throw new InvalidOperationException("Unexpected resource type in mutation result.")
        };
    }
}

public static class DeleteSeaweedFSServiceEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSeaweedFSServiceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{Id}",
                async (
                    IMediator mediator, [AsParameters] DeleteSeaweedFSServiceRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SeaweedFSServiceDto value => Results.Ok(value),
                    SeaweedFSNotFound => SeaweedFSHttpMappings.Problem(
                        404,
                        "not-found",
                        "SeaweedFS resource not found"),
                    SeaweedFSConflict error => SeaweedFSHttpMappings.Problem(
                        409,
                        error.Code,
                        "SeaweedFS resource changed or operation unavailable")
                }))
            .WithName("DeleteSeaweedFSService");
        return app;
    }
}
