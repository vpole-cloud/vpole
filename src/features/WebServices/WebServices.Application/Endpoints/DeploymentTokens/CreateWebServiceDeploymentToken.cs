using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Tokens;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;

namespace Pole.Features.WebServices.Application.Endpoints.DeploymentTokens;

public sealed record CreateWebServiceDeploymentTokenRequest(
    [FromRoute] string Id,
    [FromBody] CreateWebServiceDeploymentTokenRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default
) : IMediatorRequest<CreateWebServiceDeploymentTokenOutcome>, ITenantWriteOperation;

public union CreateWebServiceDeploymentTokenOutcome(
    CreateWebServiceDeploymentTokenResponse,
    WebServiceNotFound);

public sealed record CreateWebServiceDeploymentTokenRequestBody(string Name);

public sealed class CreateWebServiceDeploymentTokenHandler(
    WebServiceExists webServiceExists,
    PersistIssuedWebServiceDeploymentToken persistIssuedToken)
    : IMediatorOutcomeHandler<
        CreateWebServiceDeploymentTokenRequest,
        CreateWebServiceDeploymentTokenOutcome>
{
    public async Task<CreateWebServiceDeploymentTokenOutcome> HandleAsync(
        CreateWebServiceDeploymentTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        var exists = await webServiceExists(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (!exists)
            return new WebServiceNotFound(request.Id);

        var issuedToken = WebServiceDeploymentTokenFactory.Issue(
            request.TenantId,
            request.Id,
            request.Body.Name.Trim(),
            request.ActorUserId.Value,
            request.ActorEmail.Value,
            WebServiceDeploymentTokenFactory.DefaultOptions);

        await persistIssuedToken(
            issuedToken.Token,
            cancellationToken);

        return new CreateWebServiceDeploymentTokenOutcome(
            new CreateWebServiceDeploymentTokenResponse(
            issuedToken.Token.ToDto(),
            issuedToken.RawToken));
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
        app.MapPost("/{Id}/deployment-tokens", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] CreateWebServiceDeploymentTokenRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    CreateWebServiceDeploymentTokenResponse response => Results.Created(
                        $"/api/v1/web-services/{request.Id}/deployment-tokens/{response.Token.Id}",
                        response),
                    WebServiceNotFound error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("CreateWebServiceDeploymentToken")
            .Produces<CreateWebServiceDeploymentTokenResponse>(StatusCodes.Status201Created);
        return app;
    }
}
