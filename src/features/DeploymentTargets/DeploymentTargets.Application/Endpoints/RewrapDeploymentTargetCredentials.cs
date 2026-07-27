using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Security;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public static class RewrapDeploymentTargetCredentials
{
    public static IEndpointRouteBuilder MapRewrapDeploymentTargetCredentialsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/deployment-target-credentials/rewrap", async (
                IMediator mediator,
                [AsParameters] RewrapDeploymentTargetCredentialsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("RewrapDeploymentTargetCredentials")
            .RequireAuthorization(Policy.Admin)
            .Produces<DeploymentTargetCredentialRewrapSummary>();

        return app;
    }
}

public sealed record RewrapDeploymentTargetCredentialsRequestBody(
    string ProviderId,
    string KeyId,
    int KeyVersion,
    string KeyWrapAlgorithm,
    int KeyWrapAlgorithmVersion);

public sealed record RewrapDeploymentTargetCredentialsRequest(
    [FromBody] RewrapDeploymentTargetCredentialsRequestBody Body)
    : IMediatorRequest<DeploymentTargetCredentialRewrapSummary>;

public sealed class RewrapDeploymentTargetCredentialsHandler(
    IDeploymentTargetCredentialRewrapService rewrapService)
    : IMediatorHandler<RewrapDeploymentTargetCredentialsRequest, DeploymentTargetCredentialRewrapSummary>
{
    public Task<Result<DeploymentTargetCredentialRewrapSummary>> HandleAsync(
        RewrapDeploymentTargetCredentialsRequest request,
        CancellationToken cancellationToken = default) =>
        rewrapService.RewrapAllAsync(
            new EnvelopeEncryptionKeyReference(
                request.Body.ProviderId,
                request.Body.KeyId,
                request.Body.KeyVersion,
                request.Body.KeyWrapAlgorithm,
                request.Body.KeyWrapAlgorithmVersion),
            cancellationToken);
}

public sealed class RewrapDeploymentTargetCredentialsRequestValidator
    : AbstractValidator<RewrapDeploymentTargetCredentialsRequest>
{
    public RewrapDeploymentTargetCredentialsRequestValidator() =>
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new RewrapDeploymentTargetCredentialsRequestBodyValidator());
}

public sealed class RewrapDeploymentTargetCredentialsRequestBodyValidator
    : AbstractValidator<RewrapDeploymentTargetCredentialsRequestBody>
{
    public RewrapDeploymentTargetCredentialsRequestBodyValidator()
    {
        RuleFor(body => body.ProviderId).NotEmpty().MaximumLength(128);
        RuleFor(body => body.KeyId).NotEmpty().MaximumLength(128);
        RuleFor(body => body.KeyVersion).GreaterThan(0);
        RuleFor(body => body.KeyWrapAlgorithm).NotEmpty().MaximumLength(128);
        RuleFor(body => body.KeyWrapAlgorithmVersion).GreaterThan(0);
    }
}
