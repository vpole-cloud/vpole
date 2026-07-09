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

public static class WebServiceDeploymentTokens
{
    public static IEndpointRouteBuilder MapWebServiceDeploymentTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/deployment-tokens", async (IMediator mediator, [AsParameters] GetWebServiceDeploymentTokensRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetWebServiceDeploymentTokens")
            .Produces<IReadOnlyList<WebServiceDeploymentTokenDto>>();

        app.MapPost("/{Id}/deployment-tokens", async (IMediator mediator, [AsParameters] CreateWebServiceDeploymentTokenRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(response => Results.Created(
                    $"/api/v1/web-services/{request.Id}/deployment-tokens/{response.Token.Id}",
                    response));
            })
            .WithName("CreateWebServiceDeploymentToken")
            .Produces<CreateWebServiceDeploymentTokenResponse>(StatusCodes.Status201Created);

        app.MapDelete("/{Id}/deployment-tokens/{TokenId}", async (IMediator mediator, [AsParameters] RevokeWebServiceDeploymentTokenRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("RevokeWebServiceDeploymentToken")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

public sealed record GetWebServiceDeploymentTokensRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<IReadOnlyList<WebServiceDeploymentTokenDto>>, ITenantReadOperation;

public sealed record CreateWebServiceDeploymentTokenRequest(
    [FromRoute] string Id,
    [FromBody] CreateWebServiceDeploymentTokenRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<CreateWebServiceDeploymentTokenResponse>, ITenantWriteOperation;

public sealed record CreateWebServiceDeploymentTokenRequestBody(string Name);

public sealed record RevokeWebServiceDeploymentTokenRequest(
    [FromRoute] string Id,
    [FromRoute] string TokenId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default
) : IMediatorRequest<RevokeWebServiceDeploymentTokenResponse>, ITenantWriteOperation;

public sealed record RevokeWebServiceDeploymentTokenResponse(string Id);

public sealed class GetWebServiceDeploymentTokensHandler(IWebServiceDbContext dbContext)
    : IMediatorHandler<GetWebServiceDeploymentTokensRequest, IReadOnlyList<WebServiceDeploymentTokenDto>>
{
    public async Task<Result<IReadOnlyList<WebServiceDeploymentTokenDto>>> HandleAsync(
        GetWebServiceDeploymentTokensRequest request,
        CancellationToken cancellationToken = default)
    {
        var webServiceExists = await WebServiceExistsAsync(
            dbContext,
            request.TenantId,
            request.Id,
            cancellationToken);
        if (!webServiceExists)
            return Failures.NotFound("Web service not found.", request.Id);

        var filter = Builders<WebServiceDeploymentToken>.Filter.And(
            Builders<WebServiceDeploymentToken>.Filter.ForTenant(request.TenantId),
            Builders<WebServiceDeploymentToken>.Filter.Eq(token => token.WebServiceId, request.Id));

        var tokens = await dbContext.WebServiceDeploymentTokens
            .Find(filter)
            .SortByDescending(token => token.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return tokens.Select(token => token.ToDto()).ToArray();
    }

    internal static Task<bool> WebServiceExistsAsync(
        IWebServiceDbContext dbContext,
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken)
    {
        var filter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(tenantId),
            Builders<WebService>.Filter.Eq(service => service.Id, webServiceId));

        return dbContext.WebServices
            .Find(filter)
            .AnyAsync(cancellationToken);
    }
}

public sealed class CreateWebServiceDeploymentTokenHandler(IWebServiceDbContext dbContext)
    : IMediatorHandler<CreateWebServiceDeploymentTokenRequest, CreateWebServiceDeploymentTokenResponse>
{
    public async Task<Result<CreateWebServiceDeploymentTokenResponse>> HandleAsync(
        CreateWebServiceDeploymentTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        var webServiceExists = await GetWebServiceDeploymentTokensHandler.WebServiceExistsAsync(
            dbContext,
            request.TenantId,
            request.Id,
            cancellationToken);
        if (!webServiceExists)
            return Failures.NotFound("Web service not found.", request.Id);

        var issuedToken = WebServiceDeploymentTokenFactory.Issue(
            request.TenantId,
            request.Id,
            request.Body.Name.Trim(),
            request.ActorUserId.Value,
            request.ActorEmail.Value,
            WebServiceDeploymentTokenFactory.DefaultOptions);

        await dbContext.WebServiceDeploymentTokens.InsertOneAsync(
            issuedToken.Token,
            cancellationToken: cancellationToken);

        return new CreateWebServiceDeploymentTokenResponse(
            issuedToken.Token.ToDto(),
            issuedToken.RawToken);
    }
}

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

public sealed class GetWebServiceDeploymentTokensValidator : AbstractValidator<GetWebServiceDeploymentTokensRequest>
{
    public GetWebServiceDeploymentTokensValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
    }
}

public sealed class CreateWebServiceDeploymentTokenValidator : AbstractValidator<CreateWebServiceDeploymentTokenRequest>
{
    public CreateWebServiceDeploymentTokenValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Body.Name)
            .NotEmpty()
            .MaximumLength(100);
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
