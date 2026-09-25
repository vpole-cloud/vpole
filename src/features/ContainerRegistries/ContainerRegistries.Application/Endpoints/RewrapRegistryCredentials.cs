using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Mediator;
using static Pole.Features.ContainerRegistries.Application.Common.ContainerRegistryHttpMappings;

namespace Pole.Features.ContainerRegistries.Application.Endpoints;

public sealed record RewrapRegistryCredentialsRequestBody(
    string ProviderId,
    string KeyId,
    int KeyVersion,
    string KeyWrapAlgorithm,
    int KeyWrapAlgorithmVersion);

public sealed record RewrapRegistryCredentialsRequest(
    RewrapRegistryCredentialsRequestBody Body)
    : IMediatorRequest<RewrapRegistryCredentialsOutcome>;

public union RewrapRegistryCredentialsOutcome(
    RegistryCredentialRewrapSummary,
    RegistryCredentialRewrapRejected,
    RegistryCredentialRewrapFailed);

public sealed class RewrapRegistryCredentialsRequestValidator
    : AbstractValidator<RewrapRegistryCredentialsRequest>
{
    public RewrapRegistryCredentialsRequestValidator()
    {
        RuleFor(request => request.Body).NotNull().DependentRules(() =>
        {
            RuleFor(request => request.Body.ProviderId).NotEmpty().MaximumLength(128);
            RuleFor(request => request.Body.KeyId).NotEmpty().MaximumLength(128);
            RuleFor(request => request.Body.KeyVersion).GreaterThan(0);
            RuleFor(request => request.Body.KeyWrapAlgorithm).NotEmpty().MaximumLength(128);
            RuleFor(request => request.Body.KeyWrapAlgorithmVersion).GreaterThan(0);
        });
    }
}

public sealed class RewrapRegistryCredentialsHandler(
    RewrapAllRegistryCredentials rewrapCredentials)
    : IMediatorOutcomeHandler<RewrapRegistryCredentialsRequest, RewrapRegistryCredentialsOutcome>
{
    public async Task<RewrapRegistryCredentialsOutcome> HandleAsync(
        RewrapRegistryCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await rewrapCredentials(
            new EnvelopeEncryptionKeyReference(
                request.Body.ProviderId,
                request.Body.KeyId,
                request.Body.KeyVersion,
                request.Body.KeyWrapAlgorithm,
                request.Body.KeyWrapAlgorithmVersion),
            cancellationToken);
        return result switch
        {
            RegistryCredentialRewrapSummary summary => summary,
            RegistryCredentialRewrapRejected rejected => rejected,
            RegistryCredentialRewrapFailed failed => failed
        };
    }
}

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
                    new RewrapRegistryCredentialsRequest(body), cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    RegistryCredentialRewrapSummary summary => Results.Ok(summary),
                    RegistryCredentialRewrapRejected rejected => MapRewrapRejected(rejected).ToHttpResult(),
                    RegistryCredentialRewrapFailed failed => MapRewrapFailed(failed).ToHttpResult()
                });
            })
            .WithName("RewrapRegistryCredentials")
            .RequireAuthorization(Policy.Admin)
            .Produces<RegistryCredentialRewrapSummary>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }
}
