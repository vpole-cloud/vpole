using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Endpoints;

public static class RewrapRegistryCredentials
{
    public static IEndpointRouteBuilder MapRewrapRegistryCredentialsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/container-registry-credentials/rewrap", async (
                IMediator mediator,
                [FromBody] RewrapRegistryCredentialsRequestBody body,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new RewrapRegistryCredentialsRequest(body),
                    cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("RewrapRegistryCredentials")
            .RequireAuthorization(Policy.Admin)
            .Produces<RegistryCredentialRewrapSummary>();

        return app;
    }
}

public sealed record RewrapRegistryCredentialsRequestBody(
    string ProviderId,
    string KeyId,
    int KeyVersion,
    string KeyWrapAlgorithm,
    int KeyWrapAlgorithmVersion);

public sealed record RewrapRegistryCredentialsRequest(
    RewrapRegistryCredentialsRequestBody Body)
    : IMediatorRequest<RegistryCredentialRewrapSummary>;

public sealed class RewrapRegistryCredentialsHandler(
    IRegistryCredentialRewrapService rewrapService)
    : IMediatorHandler<RewrapRegistryCredentialsRequest, RegistryCredentialRewrapSummary>
{
    public Task<Result<RegistryCredentialRewrapSummary>> HandleAsync(
        RewrapRegistryCredentialsRequest request,
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
            return Task.FromResult<Result<RegistryCredentialRewrapSummary>>(
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
