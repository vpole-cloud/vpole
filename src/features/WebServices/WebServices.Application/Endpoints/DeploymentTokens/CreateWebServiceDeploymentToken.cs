using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public sealed record CreateWebServiceDeploymentTokenRequest(
    [FromRoute] string Id,
    [FromBody] CreateWebServiceDeploymentTokenRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<CreateWebServiceDeploymentTokenResponse>, ITenantWriteOperation;

public sealed record CreateWebServiceDeploymentTokenRequestBody(string Name);

public sealed class CreateWebServiceDeploymentTokenHandler(IWebServiceDbContext dbContext)
    : IMediatorHandler<CreateWebServiceDeploymentTokenRequest, CreateWebServiceDeploymentTokenResponse>
{
    public async Task<Result<CreateWebServiceDeploymentTokenResponse>> HandleAsync(
        CreateWebServiceDeploymentTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        var webServiceExists = await WebServiceDeploymentTokenAccess.WebServiceExistsAsync(
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

public static class CreateWebServiceDeploymentTokenEndpoint
{
    public static IEndpointRouteBuilder MapCreateWebServiceDeploymentTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/deployment-tokens", async (IMediator mediator,
                [AsParameters] CreateWebServiceDeploymentTokenRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(response => Results.Created(
                    $"/api/v1/web-services/{request.Id}/deployment-tokens/{response.Token.Id}", response));
            })
            .WithName("CreateWebServiceDeploymentToken")
            .Produces<CreateWebServiceDeploymentTokenResponse>(StatusCodes.Status201Created);
        return app;
    }
}
