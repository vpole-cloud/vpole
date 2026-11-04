using MongoDB.Bson;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateRepositorySourceBody(
    long ExpectedRevision,
    int PollIntervalSeconds,
    GitPrunePolicy Prune = GitPrunePolicy.Retain);

public sealed record UpdateRepositorySourceRequest(
    [FromBody] UpdateRepositorySourceBody Body,
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<UpdateRepositorySourceOutcome>, ITenantWriteOperation;

public union UpdateRepositorySourceOutcome(
    RepositorySourceView,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceInactive);

public sealed class UpdateRepositorySourceValidator : AbstractValidator<UpdateRepositorySourceRequest>
{
    public UpdateRepositorySourceValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Body).NotNull();
        When(item => item.Body is not null,
            () =>
            {
                RuleFor(item => item.Body.ExpectedRevision).GreaterThan(0);
                RuleFor(item => item.Body.PollIntervalSeconds).InclusiveBetween(60, 86400);
                RuleFor(item => item.Body.Prune).IsInEnum();
            });
    }
}

public sealed class UpdateRepositorySourceHandler(GitRepositorySources sources, GitRepositoryQueries queries)
    : IMediatorOutcomeHandler<UpdateRepositorySourceRequest, UpdateRepositorySourceOutcome>
{
    public async Task<UpdateRepositorySourceOutcome> HandleAsync(
        UpdateRepositorySourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();
        var sourceId = ObjectId.Parse(request.Id).ToString();

        var outcome = await sources.ChangePolicyAsync(new(tenantId,
                sourceId,
                request.Body.ExpectedRevision,
                new(TimeSpan.FromSeconds(request.Body.PollIntervalSeconds)), request.Body.Prune),
            cancellationToken);
        return outcome switch
        {
            RepositorySource source => await queries.SourceAsync(source.TenantId, source.Id, cancellationToken)
                is { } current ? current : new GitSourceNotFound(),
            GitSourceNotFound missing => missing,
            GitSourceRevisionConflict stale => stale,
            GitSourceInactive inactive => inactive
        };
    }
}

public static class UpdateRepositorySourceEndpoint
{
    public static IEndpointRouteBuilder MapUpdateRepositorySourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}",
                async (IMediator mediator,
                        [AsParameters] UpdateRepositorySourceRequest request, CancellationToken ct) =>
                    (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                    {
                        RepositorySourceView value => Results.Ok(value),
                        GitSourceNotFound value => RepositoryHttpMappings.Map(value),
                        GitSourceRevisionConflict value => RepositoryHttpMappings.Map(value),
                        GitSourceInactive inactive => RepositoryHttpMappings.Map(inactive)
                    })).WithName("UpdateGitRepositorySource")
            .WithMetadata(new RequestSizeLimitAttribute(SnapshotHttpLimits.MaximumSourceRequestBytes));
        return app;
    }
}
