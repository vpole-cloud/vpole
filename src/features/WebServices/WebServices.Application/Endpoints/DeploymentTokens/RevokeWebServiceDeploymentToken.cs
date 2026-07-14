using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Application.Common;
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

public sealed class RevokeWebServiceDeploymentTokenHandler(IWebServiceDbContext dbContext)
    : IMediatorHandler<RevokeWebServiceDeploymentTokenRequest, RevokeWebServiceDeploymentTokenResponse>
{
    public async Task<Result<RevokeWebServiceDeploymentTokenResponse>> HandleAsync(
        RevokeWebServiceDeploymentTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<WebServiceDeploymentToken>.Filter.And(
            Builders<WebServiceDeploymentToken>.Filter.ForTenant(request.TenantId),
            Builders<WebServiceDeploymentToken>.Filter.Eq(token => token.WebServiceId, request.Id),
            Builders<WebServiceDeploymentToken>.Filter.Eq(token => token.Id, request.TokenId));

        var update = Builders<WebServiceDeploymentToken>.Update
            .Set(token => token.RevokedAtUtc, DateTime.UtcNow)
            .Set(token => token.RevokedByUserId, request.ActorUserId.Value);

        var result = await dbContext.WebServiceDeploymentTokens.UpdateOneAsync(
            filter,
            update,
            cancellationToken: cancellationToken);

        return result.MatchedCount == 0
            ? Failures.NotFound("Deployment token not found.", request.TokenId)
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
