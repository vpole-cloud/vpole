using MongoDB.Bson;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetryRepositorySourceBody(
    long ExpectedRevision,
    Guid IdempotencyKey,
    string RunId);

public sealed record RetryRepositorySourceRequest(
    [FromBody] RetryRepositorySourceBody Body,
    [FromRoute] string Id,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<RetryRepositorySourceOutcome>, ITenantWriteOperation;

public union RetryRepositorySourceOutcome(
    GitSourceScheduled,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceIdempotencyConflict,
    GitSourceBusy,
    GitRetryNotAvailable);

public sealed class RetryRepositorySourceValidator : AbstractValidator<RetryRepositorySourceRequest>
{
    public RetryRepositorySourceValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Body).NotNull();
        When(item => item.Body is not null,
            () =>
            {
                RuleFor(item => item.Body.ExpectedRevision).GreaterThan(0);
                RuleFor(item => item.Body.IdempotencyKey).NotEmpty();
                RuleFor(item => item.Body.RunId).MongoObjectId("Run ID");
            });
    }
}

public sealed class RetryRepositorySourceHandler(GitRepositorySources sources)
    : IMediatorOutcomeHandler<RetryRepositorySourceRequest, RetryRepositorySourceOutcome>
{
    public async Task<RetryRepositorySourceOutcome> HandleAsync(
        RetryRepositorySourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();
        var sourceId = ObjectId.Parse(request.Id).ToString();
        var runId = ObjectId.Parse(request.Body.RunId).ToString();

        var command = new GitSourceCommand(tenantId,
            sourceId,
            request.Body.ExpectedRevision,
            request.Body.IdempotencyKey.ToString("D"),
            new(GitRunTrigger.Manual, request.CurrentUserId.Value));
        var outcome = await sources.RetryAsync(new(command, runId), cancellationToken);
        return outcome switch
        {
            GitSourceScheduled value => value,
            GitSourceNotFound value => value,
            GitSourceRevisionConflict value => value,
            GitSourceIdempotencyConflict value => value,
            GitSourceBusy value => value,
            GitRetryNotAvailable value => value
        };
    }
}

public static class RetryRepositorySourceEndpoint
{
    public static IEndpointRouteBuilder MapRetryRepositorySourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/retry",
                async (IMediator mediator,
                        [AsParameters] RetryRepositorySourceRequest request, CancellationToken ct) =>
                    (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                    {
                        GitSourceScheduled value => Results.Accepted(value: value),
                        GitSourceNotFound value => RepositoryHttpMappings.Map(value),
                        GitSourceRevisionConflict value => RepositoryHttpMappings.Map(value),
                        GitSourceIdempotencyConflict value => RepositoryHttpMappings.Map(value),
                        GitSourceBusy value => RepositoryHttpMappings.Map(value),
                        GitRetryNotAvailable value => RepositoryHttpMappings.Map(value)
                    })).WithName("RetryGitRepositorySource")
            .WithMetadata(new RequestSizeLimitAttribute(SnapshotHttpLimits.MaximumSourceRequestBytes));
        return app;
    }
}
