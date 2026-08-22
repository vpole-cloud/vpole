using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Functions.Application.Endpoints;

public static class GetFunctionAppById
{
    public static IEndpointRouteBuilder MapGetFunctionAppByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetFunctionAppByIdRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    FunctionAppDetailsDto details => Results.Ok(details),
                    FunctionAppNotFound notFound => httpProblems.Map(notFound).ToHttpResult()
                });
            })
            .WithName("GetFunctionAppById")
            .Produces<FunctionAppDetailsDto>();
        return app;
    }
}

public sealed record GetFunctionAppByIdRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetFunctionAppByIdOutcome>, ITenantReadOperation;

public union GetFunctionAppByIdOutcome(
    FunctionAppDetailsDto,
    FunctionAppNotFound);

public sealed class GetFunctionAppByIdHandler(
    LoadFunctionAppById loadFunctionAppById,
    LoadFunctionAppRevisionReferences loadFunctionAppRevisionReferences)
    : IMediatorOutcomeHandler<GetFunctionAppByIdRequest, GetFunctionAppByIdOutcome>
{
    public async Task<GetFunctionAppByIdOutcome> HandleAsync(
        GetFunctionAppByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var functionApp = await loadFunctionAppById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (functionApp is null)
            return new FunctionAppNotFound(request.Id);

        var references = await loadFunctionAppRevisionReferences(functionApp, cancellationToken);
        return new FunctionAppDetailsDto(
            functionApp.ToDto(),
            references.ActiveRevision?.ToDto(),
            references.PendingRevision?.ToDto());
    }
}

public sealed class GetFunctionAppByIdRequestValidator
    : AbstractValidator<GetFunctionAppByIdRequest>
{
    public GetFunctionAppByIdRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Function App ID");
}
