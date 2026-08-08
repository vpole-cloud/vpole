using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public sealed record RevokeWebServiceDeploymentTokenRequest(
    [FromRoute] string Id,
    [FromRoute] string TokenId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default
) : IMediatorRequest<RevokeWebServiceDeploymentTokenResponse>, ITenantWriteOperation;

public sealed record RevokeWebServiceDeploymentTokenResponse(string Id);

public sealed class RevokeWebServiceDeploymentTokenHandler(
    RevokeWebServiceDeploymentToken revokeToken)
    : IMediatorHandler<RevokeWebServiceDeploymentTokenRequest, RevokeWebServiceDeploymentTokenResponse>
{
    public async Task<Result<RevokeWebServiceDeploymentTokenResponse>> HandleAsync(
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
        return result is Failure failure
            ? failure
            : new RevokeWebServiceDeploymentTokenResponse(request.TokenId);
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
        app.MapDelete("/{Id}/deployment-tokens/{TokenId}", async (IMediator mediator,
                [AsParameters] RevokeWebServiceDeploymentTokenRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("RevokeWebServiceDeploymentToken")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}
