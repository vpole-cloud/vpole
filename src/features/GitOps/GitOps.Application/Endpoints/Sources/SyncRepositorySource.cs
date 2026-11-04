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
public sealed record SyncRepositorySourceBody(
    long ExpectedRevision,
    Guid IdempotencyKey);

public sealed record SyncRepositorySourceRequest(
    [FromBody] SyncRepositorySourceBody Body,
    [FromRoute] string Id,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<SyncRepositorySourceOutcome>, ITenantWriteOperation;

public union SyncRepositorySourceOutcome(
    GitSourceScheduled,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceIdempotencyConflict,
    GitSourceInactive);

public sealed class SyncRepositorySourceValidator : AbstractValidator<SyncRepositorySourceRequest>
{
    public SyncRepositorySourceValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Body).NotNull();
        When(item => item.Body is not null,
            () =>
            {
                RuleFor(item => item.Body.ExpectedRevision).GreaterThan(0);
                RuleFor(item => item.Body.IdempotencyKey).NotEmpty();
            });
    }
}

public sealed class SyncRepositorySourceHandler(GitRepositorySources sources)
    : IMediatorOutcomeHandler<SyncRepositorySourceRequest, SyncRepositorySourceOutcome>
{
    public async Task<SyncRepositorySourceOutcome> HandleAsync(
        SyncRepositorySourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();
        var sourceId = ObjectId.Parse(request.Id).ToString();

        var command = new GitSourceCommand(tenantId,
            sourceId,
            request.Body.ExpectedRevision,
            request.Body.IdempotencyKey.ToString("D"),
            new(GitRunTrigger.Manual, request.CurrentUserId.Value));
        var outcome = await sources.RequestSyncAsync(command, cancellationToken);
        return outcome switch
        {
            GitSourceScheduled value => value,
            GitSourceNotFound value => value,
            GitSourceRevisionConflict value => value,
            GitSourceInactive inactive => inactive,
            GitSourceIdempotencyConflict value => value
        };
    }
}

public static class SyncRepositorySourceEndpoint
{
    public static IEndpointRouteBuilder MapSyncRepositorySourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/sync",
                async (IMediator mediator,
                        [AsParameters] SyncRepositorySourceRequest request, CancellationToken ct) =>
                    (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                    {
                        GitSourceScheduled value => Results.Accepted(value: value),
                        GitSourceNotFound value => RepositoryHttpMappings.Map(value),
                        GitSourceRevisionConflict value => RepositoryHttpMappings.Map(value),
                        GitSourceInactive inactive => RepositoryHttpMappings.Map(inactive),
                        GitSourceIdempotencyConflict value => RepositoryHttpMappings.Map(value)
                    })).WithName("SyncGitRepositorySource")
            .WithMetadata(new RequestSizeLimitAttribute(SnapshotHttpLimits.MaximumSourceRequestBytes));
        return app;
    }
}
