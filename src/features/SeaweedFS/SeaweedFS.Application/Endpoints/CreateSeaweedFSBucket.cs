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

public sealed record CreateSeaweedFSBucketBody(
    string Name
);

public union CreateSeaweedFSBucketOutcome(
    SeaweedFSBucketDto,
    SeaweedFSNotFound,
    SeaweedFSConflict,
    SeaweedFSCredentialFailure);

public sealed record CreateSeaweedFSBucketRequest(
    [FromRoute] string Id,
    [FromBody] CreateSeaweedFSBucketBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<CreateSeaweedFSBucketOutcome>, ITenantWriteOperation;

public sealed class CreateSeaweedFSBucketValidator : AbstractValidator<CreateSeaweedFSBucketRequest>
{
    public CreateSeaweedFSBucketValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
        RuleFor(x => x.Body)
            .NotNull()
            .DependentRules(() =>
            {
                RuleFor(x => x.Body.Name)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty()
                    .Length(3, 63)
                    .Matches(@"\A[a-z0-9][a-z0-9.-]*[a-z0-9]\z")
                    .Must(name => !name.Contains("..", StringComparison.Ordinal) &&
                        !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[0-9]+(?:\.[0-9]+){3}\z"))
                    .Must(name => name != "filemeta" && !name.StartsWith("xn--", StringComparison.Ordinal) &&
                        !name.EndsWith("-s3alias", StringComparison.Ordinal))
                    .WithMessage("This name is reserved by SeaweedFS.");
            });
    }
}

public sealed class CreateSeaweedFSBucketHandler(
    SeaweedFSMutations mutations
) : IMediatorOutcomeHandler<CreateSeaweedFSBucketRequest, CreateSeaweedFSBucketOutcome>
{
    public async Task<CreateSeaweedFSBucketOutcome> HandleAsync(
        CreateSeaweedFSBucketRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await mutations.CreateBucketAsync(
            request.TenantId,
            request.Id,
            request.Body.Name,
            cancellationToken);
        return result switch
        {
            SeaweedFSBucketDto value => value,
            SeaweedFSNotFound value => value,
            SeaweedFSConflict value => value,
            SeaweedFSCredentialFailure value => value,
            SeaweedFSServiceDto => throw new InvalidOperationException("Unexpected resource type in mutation result.")
        };
    }
}

public static class CreateSeaweedFSBucketEndpoint
{
    public static IEndpointRouteBuilder MapCreateSeaweedFSBucketEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/buckets",
                async (
                    IMediator mediator, [AsParameters] CreateSeaweedFSBucketRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SeaweedFSBucketDto value => Results.Ok(value),
                    SeaweedFSNotFound => SeaweedFSHttpMappings.Problem(
                        404,
                        "not-found",
                        "SeaweedFS resource not found"),
                    SeaweedFSConflict error => SeaweedFSHttpMappings.Problem(
                        409,
                        error.Code,
                        "SeaweedFS resource changed or operation unavailable"),
                    SeaweedFSCredentialFailure => SeaweedFSHttpMappings.Problem(
                        503,
                        "credentials-unavailable",
                        "Credential operation unavailable")
                }))
            .WithName("CreateSeaweedFSBucket");
        return app;
    }
}
