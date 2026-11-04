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

public sealed record GetRepositorySourceRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetRepositorySourceOutcome>, ITenantReadOperation;

public union GetRepositorySourceOutcome(
    RepositorySourceView,
    GitSourceNotFound);

public sealed class GetRepositorySourceValidator : AbstractValidator<GetRepositorySourceRequest>
{
    public GetRepositorySourceValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
    }
}

public sealed class GetRepositorySourceHandler(GitRepositoryQueries queries)
    : IMediatorOutcomeHandler<GetRepositorySourceRequest, GetRepositorySourceOutcome>
{
    public async Task<GetRepositorySourceOutcome> HandleAsync(
        GetRepositorySourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();
        var sourceId = ObjectId.Parse(request.Id).ToString();

        var result = await queries.SourceAsync(tenantId, sourceId, cancellationToken);
        return result is null ? new GitSourceNotFound() : result;
    }
}

public static class GetRepositorySourceEndpoint
{
    public static IEndpointRouteBuilder MapGetRepositorySourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}",
            async (IMediator mediator,
                    [AsParameters] GetRepositorySourceRequest request, CancellationToken ct) =>
                (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    RepositorySourceView value => Results.Ok(value),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing)
                })).WithName("GetRepositorySource");
        return app;
    }
}
