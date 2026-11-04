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
public sealed record CreateRepositorySourceBody(
    Guid IdempotencyKey,
    string Provider,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string ManifestDirectory,
    int PollIntervalSeconds = 300,
    GitPrunePolicy Prune = GitPrunePolicy.Retain);

public sealed record CreateRepositorySourceRequest(
    [FromBody] CreateRepositorySourceBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId)
    : IMediatorRequest<CreateRepositorySourceOutcome>, ITenantCreateOperation;

public union CreateRepositorySourceOutcome(
    RepositorySourceView,
    GitSourceIdempotencyConflict,
    GitSourceCreationDeleted,
    RepositoryProviderUnsupported,
    RepositoryAccessDenied,
    RepositoryNotFound,
    RepositoryTemporarilyUnavailable);

public sealed class CreateRepositorySourceValidator : AbstractValidator<CreateRepositorySourceRequest>
{
    public CreateRepositorySourceValidator()
    {
        RuleFor(item => item.Body).NotNull();
        When(item => item.Body is not null,
            () =>
            {
                RuleFor(item => item.Body.IdempotencyKey).NotEmpty();
                RuleFor(item => item.Body.Provider).NotEmpty().MaximumLength(63);
                RuleFor(item => item.Body.ConnectionId).MongoObjectId("Connection ID");
                RuleFor(item => item.Body.ExternalRepositoryId).NotEmpty().MaximumLength(128);
                RuleFor(item => item.Body.Branch).NotEmpty().MaximumLength(255);
                RuleFor(item => item.Body.ManifestDirectory).Must(RepositoryManifestDirectory.IsValid)
                    .MaximumLength(1024);
                RuleFor(item => item.Body.PollIntervalSeconds).GreaterThanOrEqualTo(60).LessThanOrEqualTo(86400);
                RuleFor(item => item.Body.Prune).IsInEnum();
            });
    }
}

public sealed class CreateRepositorySourceHandler(
    RepositoryProviderRegistry providers,
    GitRepositorySources sources,
    GitRepositoryQueries queries,
    TimeProvider clock) : IMediatorOutcomeHandler<CreateRepositorySourceRequest, CreateRepositorySourceOutcome>
{
    public async Task<CreateRepositorySourceOutcome> HandleAsync(
        CreateRepositorySourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();

        var body = request.Body with { ConnectionId = ObjectId.Parse(request.Body.ConnectionId).ToString() };
        var provider = providers.Find(body.Provider);
        if (provider is null)
            return new RepositoryProviderUnsupported();

        var access = await provider.CheckAccessAsync(new(tenantId,
                body.ConnectionId,
                body.ExternalRepositoryId),
            cancellationToken);
        if (access is not RepositoryAccessGranted granted)
            return access switch
            {
                RepositoryAccessDenied denied => denied,
                RepositoryNotFound missing => missing,
                RepositoryTemporarilyUnavailable unavailable => unavailable,
                RepositoryAccessGranted => throw new InvalidOperationException("Access was already handled.")
            };

        var outcome = await sources.CreateAsync(new(tenantId,
                body.IdempotencyKey.ToString("D"),
                new(body.Provider, body.ConnectionId, body.ExternalRepositoryId, body.Branch, body.ManifestDirectory),
                new(TimeSpan.FromSeconds(body.PollIntervalSeconds)),
                new(request.CurrentUserId.Value,
                    clock.GetUtcNow().UtcDateTime),
                granted.DisplayUrl, body.Prune),
            cancellationToken);
        return outcome switch
        {
            RepositorySource source => await queries.SourceAsync(source.TenantId, source.Id, cancellationToken)
                is { } current ? current : new GitSourceCreationDeleted(),
            GitSourceIdempotencyConflict conflict => conflict,
            GitSourceCreationDeleted deleted => deleted
        };
    }
}

public static class CreateRepositorySourceEndpoint
{
    public static IEndpointRouteBuilder MapCreateRepositorySourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/",
                async (IMediator mediator, [AsParameters] CreateRepositorySourceRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    RepositorySourceView source => Results.Created($"/api/v1/gitops/sources/{source.Id}", source),
                    GitSourceIdempotencyConflict conflict => RepositoryHttpMappings.Map(conflict),
                    GitSourceCreationDeleted deleted => RepositoryHttpMappings.Map(deleted),
                    RepositoryProviderUnsupported unsupported => RepositoryHttpMappings.Map(unsupported),
                    RepositoryAccessDenied denied => RepositoryHttpMappings.Map(denied),
                    RepositoryNotFound missing => RepositoryHttpMappings.Map(missing),
                    RepositoryTemporarilyUnavailable unavailable => RepositoryHttpMappings.Map(unavailable)
                })).WithName("CreateGitRepositorySource")
            .WithMetadata(new RequestSizeLimitAttribute(SnapshotHttpLimits.MaximumSourceRequestBytes));
        return app;
    }
}
