using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Revisions;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Functions.Application.Common.FunctionAppHttpMappings;

namespace Pole.Features.Functions.Application.Endpoints;

public static class GetFunctionRevisions
{
    public static IEndpointRouteBuilder MapGetFunctionRevisionsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/revisions",
                async (
                    IMediator mediator, [AsParameters] GetFunctionRevisionsRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        IReadOnlyList<FunctionRevisionDto> revisions => Results.Ok(revisions),
                        FunctionAppNotFound notFound => MapNotFound(notFound),
                    });
                })
            .WithName("GetFunctionRevisions")
            .Produces<IReadOnlyList<FunctionRevisionDto>>();
        return app;
    }
}

public sealed record GetFunctionRevisionsRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetFunctionRevisionsOutcome>, ITenantReadOperation;

public union GetFunctionRevisionsOutcome(IReadOnlyList<FunctionRevisionDto>, FunctionAppNotFound);

public sealed class GetFunctionRevisionsHandler(
    LoadFunctionAppById loadFunctionAppById,
    LoadFunctionAppRevisions loadFunctionAppRevisions
) : IMediatorOutcomeHandler<GetFunctionRevisionsRequest, GetFunctionRevisionsOutcome>
{
    public async Task<GetFunctionRevisionsOutcome> HandleAsync(
        GetFunctionRevisionsRequest request,
        CancellationToken cancellationToken = default)
    {
        var functionApp = await loadFunctionAppById(request.TenantId, request.Id, cancellationToken);
        if (functionApp is null)
            return new FunctionAppNotFound(request.Id);

        var revisions = await loadFunctionAppRevisions(functionApp, cancellationToken);
        return revisions.Select(revision => revision.ToDto()).ToArray();
    }
}

public sealed class GetFunctionRevisionsRequestValidator : AbstractValidator<GetFunctionRevisionsRequest>
{
    public GetFunctionRevisionsRequestValidator() => RuleFor(request => request.Id).MongoObjectId("Function App ID");
}
