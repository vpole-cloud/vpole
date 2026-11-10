using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

public sealed record CreatePrivateAccessSessionRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue SubjectUserId
) : IMediatorRequest<CreatePrivateAccessSessionOutcome>, ITenantReadOperation;

public sealed record CreatePrivateAccessSessionResponse(
    string SessionId,
    string Ticket,
    DateTime ExpiresAtUtc
)
{
    public override string ToString() =>
        $"CreatePrivateAccessSessionResponse {{ SessionId = {SessionId}, Ticket = [redacted], ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

public union CreatePrivateAccessSessionOutcome(
    CreatePrivateAccessSessionResponse,
    PrivateAccessGrantUnavailable,
    PrivateAccessSessionSubjectMismatch,
    PrivateAccessSubjectMembershipRequired,
    PrivateAccessDeploymentUnavailable,
    PrivateAccessEndpointUnavailable,
    PrivateAccessBindingChanged);

public sealed class CreatePrivateAccessSessionHandler(
    IssuePrivateAccessSession issue
) : IMediatorOutcomeHandler<CreatePrivateAccessSessionRequest, CreatePrivateAccessSessionOutcome>
{
    public async Task<CreatePrivateAccessSessionOutcome> HandleAsync(
        CreatePrivateAccessSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var outcome = await issue(request.TenantId, request.Id, request.SubjectUserId.Value, cancellationToken);
        return outcome switch
        {
            PrivateAccessSessionIssued issued => new CreatePrivateAccessSessionResponse(
                issued.SessionId,
                issued.Ticket,
                issued.ExpiresAtUtc),
            PrivateAccessGrantUnavailable error => error,
            PrivateAccessSessionSubjectMismatch error => error,
            PrivateAccessSubjectMembershipRequired error => error,
            PrivateAccessDeploymentUnavailable error => error,
            PrivateAccessEndpointUnavailable error => error,
            PrivateAccessBindingChanged error => error,
        };
    }
}

public sealed class CreatePrivateAccessSessionValidator : AbstractValidator<CreatePrivateAccessSessionRequest>
{
    public CreatePrivateAccessSessionValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Grant ID");
    }
}

public static class CreatePrivateAccessSessionEndpoint
{
    public static IEndpointRouteBuilder MapCreatePrivateAccessSessionEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/sessions",
                async (
                    IMediator mediator, HttpContext httpContext,
                    [AsParameters] CreatePrivateAccessSessionRequest request, CancellationToken cancellationToken) =>
                {
                    httpContext.Response.Headers.CacheControl = "no-store";
                    return (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        CreatePrivateAccessSessionResponse issued => Results.Json(
                            issued,
                            statusCode: StatusCodes.Status201Created),
                        PrivateAccessGrantUnavailable error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessSessionSubjectMismatch error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessSubjectMembershipRequired error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessDeploymentUnavailable error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessEndpointUnavailable error => PrivateAccessHttpProblemMappings.Map(error),
                        PrivateAccessBindingChanged error => PrivateAccessHttpProblemMappings.Map(error),
                    });
                })
            .WithName("CreatePrivateAccessSession")
            .Produces<CreatePrivateAccessSessionResponse>(StatusCodes.Status201Created);
        return app;
    }
}
