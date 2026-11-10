using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Sources;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

public sealed record GetRepositoryRunRequest(
    [FromRoute] string Id,
    [FromRoute] string RunId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetRepositoryRunOutcome>, ITenantReadOperation;

public union GetRepositoryRunOutcome(RepositoryRunView, GitSourceNotFound);

public sealed class GetRepositoryRunValidator : AbstractValidator<GetRepositoryRunRequest>
{
    public GetRepositoryRunValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.RunId).MongoObjectId("Run ID");
    }
}

public sealed class GetRepositoryRunHandler(
    GitRepositoryQueries queries
) : IMediatorOutcomeHandler<GetRepositoryRunRequest, GetRepositoryRunOutcome>
{
    public async Task<GetRepositoryRunOutcome> HandleAsync(
        GetRepositoryRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();
        var sourceId = ObjectId.Parse(request.Id).ToString();
        var runId = ObjectId.Parse(request.RunId).ToString();

        var result = await queries.RunAsync(tenantId, sourceId, runId, cancellationToken);
        return result is null ? new GitSourceNotFound() : result;
    }
}

public static class GetRepositoryRunEndpoint
{
    public static IEndpointRouteBuilder MapGetRepositoryRunEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/runs/{RunId}",
                async (
                    IMediator mediator, [AsParameters] GetRepositoryRunRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    RepositoryRunView value => Results.Ok(value),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                }))
            .WithName("GetRepositoryRun");
        return app;
    }
}
