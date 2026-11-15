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

public sealed record UpdateSeaweedFSServiceBody(
    long ExpectedRevision,
    ImmutableHashSet<string> Tags
);

public union UpdateSeaweedFSServiceOutcome(
    SeaweedFSServiceDto,
    SeaweedFSNotFound,
    SeaweedFSConflict);

public sealed record UpdateSeaweedFSServiceRequest(
    [FromRoute] string Id,
    [FromBody] UpdateSeaweedFSServiceBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<UpdateSeaweedFSServiceOutcome>, ITenantWriteOperation;

public sealed class UpdateSeaweedFSServiceValidator : AbstractValidator<UpdateSeaweedFSServiceRequest>
{
    public UpdateSeaweedFSServiceValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
        RuleFor(x => x.Body)
            .NotNull()
            .DependentRules(() =>
            {
                RuleFor(x => x.Body.ExpectedRevision).GreaterThan(0);
                RuleFor(x => x.Body.Tags).NotNull();
            });
    }
}

public sealed class UpdateSeaweedFSServiceHandler(
    SeaweedFSMutations mutations
) : IMediatorOutcomeHandler<UpdateSeaweedFSServiceRequest, UpdateSeaweedFSServiceOutcome>
{
    public async Task<UpdateSeaweedFSServiceOutcome> HandleAsync(
        UpdateSeaweedFSServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await mutations.UpdateAsync(
            request.TenantId,
            request.Id,
            request.Body.ExpectedRevision,
            request.Body.Tags,
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

public static class UpdateSeaweedFSServiceEndpoint
{
    public static IEndpointRouteBuilder MapUpdateSeaweedFSServiceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut(
                "/{Id}",
                async (
                    IMediator mediator, [AsParameters] UpdateSeaweedFSServiceRequest request,
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
            .WithName("UpdateSeaweedFSService");
        return app;
    }
}
