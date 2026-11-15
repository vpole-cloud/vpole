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

public sealed record RestoreSeaweedFSServiceBody(
    long ExpectedRevision
);

public union RestoreSeaweedFSServiceOutcome(
    SeaweedFSServiceDto,
    SeaweedFSNotFound,
    SeaweedFSConflict);

public sealed record RestoreSeaweedFSServiceRequest(
    [FromRoute] string Id,
    [FromBody] RestoreSeaweedFSServiceBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<RestoreSeaweedFSServiceOutcome>, ITenantWriteOperation;

public sealed class RestoreSeaweedFSServiceValidator : AbstractValidator<RestoreSeaweedFSServiceRequest>
{
    public RestoreSeaweedFSServiceValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
        RuleFor(x => x.Body).NotNull().DependentRules(() => { RuleFor(x => x.Body.ExpectedRevision).GreaterThan(0); });
    }
}

public sealed class RestoreSeaweedFSServiceHandler(
    SeaweedFSMutations mutations
) : IMediatorOutcomeHandler<RestoreSeaweedFSServiceRequest, RestoreSeaweedFSServiceOutcome>
{
    public async Task<RestoreSeaweedFSServiceOutcome> HandleAsync(
        RestoreSeaweedFSServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await mutations.RestoreAsync(
            request.TenantId,
            request.Id,
            request.Body.ExpectedRevision,
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

public static class RestoreSeaweedFSServiceEndpoint
{
    public static IEndpointRouteBuilder MapRestoreSeaweedFSServiceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/restore",
                async (
                    IMediator mediator, [AsParameters] RestoreSeaweedFSServiceRequest request,
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
            .WithName("RestoreSeaweedFSService");
        return app;
    }
}
