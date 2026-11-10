using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResumeRepositorySourceBody(
    long ExpectedRevision
);

public sealed record ResumeRepositorySourceRequest(
    [FromBody] ResumeRepositorySourceBody Body,
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId
) : IMediatorRequest<ResumeRepositorySourceOutcome>, ITenantWriteOperation;

public union ResumeRepositorySourceOutcome(
    RepositorySourceView,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceInactive);

public sealed class ResumeRepositorySourceValidator : AbstractValidator<ResumeRepositorySourceRequest>
{
    public ResumeRepositorySourceValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Body).NotNull();
        When(item => item.Body is not null, () => RuleFor(item => item.Body.ExpectedRevision).GreaterThan(0));
    }
}

public sealed class ResumeRepositorySourceHandler(
    GitSourceLifecycleStore lifecycle,
    GitRepositoryQueries queries
) : IMediatorOutcomeHandler<ResumeRepositorySourceRequest, ResumeRepositorySourceOutcome>
{
    public async Task<ResumeRepositorySourceOutcome> HandleAsync(
        ResumeRepositorySourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();
        var sourceId = ObjectId.Parse(request.Id).ToString();
        var outcome = await lifecycle.ResumeAsync(
            new ChangeGitSourceActivity(tenantId, sourceId, request.Body.ExpectedRevision, request.CurrentUserId.Value),
            cancellationToken);
        return outcome switch
        {
            RepositorySource source => await queries.SourceAsync(source.TenantId, source.Id, cancellationToken) is
                { } current
                ? current
                : new GitSourceNotFound(),
            GitSourceNotFound missing => missing,
            GitSourceRevisionConflict stale => stale,
            GitSourceInactive inactive => inactive,
        };
    }
}

public static class ResumeRepositorySourceEndpoint
{
    public static IEndpointRouteBuilder MapResumeRepositorySourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/resume",
                async (
                    IMediator mediator, [AsParameters] ResumeRepositorySourceRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    RepositorySourceView value => Results.Ok(value),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                    GitSourceRevisionConflict stale => RepositoryHttpMappings.Map(stale),
                    GitSourceInactive inactive => RepositoryHttpMappings.Map(inactive),
                }))
            .WithName("ResumeGitRepositorySource")
            .WithMetadata(new RequestSizeLimitAttribute(SnapshotHttpLimits.MaximumSourceRequestBytes));
        return app;
    }
}
