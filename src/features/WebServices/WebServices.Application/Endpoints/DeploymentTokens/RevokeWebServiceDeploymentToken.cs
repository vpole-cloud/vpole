using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Application.Persistence.Tokens;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints.DeploymentTokens;

public sealed record RevokeWebServiceDeploymentTokenRequest(
    [FromRoute] string Id,
    [FromRoute] string TokenId,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default
) : IMediatorRequest<RevokeWebServiceDeploymentTokenOutcome>, ITenantWriteOperation;

public union RevokeWebServiceDeploymentTokenOutcome(
    RevokeWebServiceDeploymentTokenResponse,
    WebServiceDeploymentTokenNotFound);

public sealed record RevokeWebServiceDeploymentTokenResponse(string Id);

public sealed class RevokeWebServiceDeploymentTokenHandler(
    Persistence.Tokens.RevokeWebServiceDeploymentTokenOutcome revokeToken)
    : IMediatorOutcomeHandler<
        RevokeWebServiceDeploymentTokenRequest,
        RevokeWebServiceDeploymentTokenOutcome>
{
    public async Task<RevokeWebServiceDeploymentTokenOutcome> HandleAsync(
        RevokeWebServiceDeploymentTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await revokeToken(
            request.TenantId,
            request.Id,
            request.TokenId,
            request.ActorUserId.Value,
            DateTime.UtcNow,
            cancellationToken);
        return result.TryGetValue(out _, out var error)
            ? new RevokeWebServiceDeploymentTokenOutcome(
                new RevokeWebServiceDeploymentTokenResponse(request.TokenId))
            : new RevokeWebServiceDeploymentTokenOutcome(error);
    }
}

public sealed class RevokeWebServiceDeploymentTokenValidator : AbstractValidator<RevokeWebServiceDeploymentTokenRequest>
{
    public RevokeWebServiceDeploymentTokenValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.TokenId).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
    }
}

public static class RevokeWebServiceDeploymentTokenEndpoint
{
    public static IEndpointRouteBuilder MapRevokeWebServiceDeploymentTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}/deployment-tokens/{TokenId}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] RevokeWebServiceDeploymentTokenRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    RevokeWebServiceDeploymentTokenResponse => Results.NoContent(),
                    WebServiceDeploymentTokenNotFound error =>
                        httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("RevokeWebServiceDeploymentToken")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}
