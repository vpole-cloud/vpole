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
                [FromBody] RewrapDeploymentTargetCredentialsRequestBody body,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new RewrapDeploymentTargetCredentialsRequest(body),
                    cancellationToken);
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
    RewrapDeploymentTargetCredentialsRequestBody Body)
    : IMediatorRequest<DeploymentTargetCredentialRewrapSummary>;

public sealed class RewrapDeploymentTargetCredentialsHandler(
    IDeploymentTargetCredentialRewrapService rewrapService)
    : IMediatorHandler<RewrapDeploymentTargetCredentialsRequest, DeploymentTargetCredentialRewrapSummary>
{
    public Task<Result<DeploymentTargetCredentialRewrapSummary>> HandleAsync(
        RewrapDeploymentTargetCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Body is null ||
            string.IsNullOrWhiteSpace(request.Body.ProviderId) ||
            request.Body.ProviderId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Body.KeyId) ||
            request.Body.KeyId.Length > 128 ||
            request.Body.KeyVersion < 1 ||
            string.IsNullOrWhiteSpace(request.Body.KeyWrapAlgorithm) ||
            request.Body.KeyWrapAlgorithm.Length > 128 ||
            request.Body.KeyWrapAlgorithmVersion < 1)
        {
            return Task.FromResult<Result<DeploymentTargetCredentialRewrapSummary>>(
                Failures.ValidationFailure(new Dictionary<string, string[]>
                {
                    ["Body"] =
                    [
                        "ProviderId, KeyId, KeyWrapAlgorithm, and positive key and algorithm versions are required."
                    ]
                }));
        }

        return rewrapService.RewrapAllAsync(
            new EnvelopeEncryptionKeyReference(
                request.Body.ProviderId,
                request.Body.KeyId,
                request.Body.KeyVersion,
                request.Body.KeyWrapAlgorithm,
                request.Body.KeyWrapAlgorithmVersion),
            cancellationToken);
    }
}
