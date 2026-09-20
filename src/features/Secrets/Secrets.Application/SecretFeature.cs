using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Application.Endpoints;
using Pole.Features.Secrets.Application.Persistence;

namespace Pole.Features.Secrets.Application;

public static class SecretFeature
{
    public static IServiceCollection AddSecretsFeature(this IServiceCollection services)
    {
        services.AddSecretPersistence();
        services.AddHttpProblemMappings(
        [
            HttpProblemMapping.For<SecretMissing>(_ => new(
                404,
                PoleProblemTypeId.FromCode("secrets.not-found"),
                "Secret not found",
                "secrets.not-found")),
            HttpProblemMapping.For<SecretNameAlreadyExists>(_ => new(
                409,
                PoleProblemTypeId.FromCode("secrets.name-already-exists"),
                "Secret name already exists in this tenant",
                "secrets.name-already-exists")),
            HttpProblemMapping.For<SecretReferenceRejected>(_ => new(
                422,
                PoleProblemTypeId.FromCode("secrets.reference-rejected"),
                "Secret reference is unavailable or outside its scope",
                "secrets.reference-rejected")),
            HttpProblemMapping.For<SecretEncryptionFailed>(_ => new(
                503,
                PoleProblemTypeId.FromCode("secrets.encryption-unavailable"),
                "Secret encryption unavailable",
                "secrets.encryption-unavailable")),
            HttpProblemMapping.For<SecretVersionConflict>(_ => new(
                409,
                PoleProblemTypeId.FromCode("secrets.version-conflict"),
                "Secret version changed; reload before rotating",
                "secrets.version-conflict")),
            HttpProblemMapping.For<SecretMappedKeysRequired>(_ => new(
                409,
                PoleProblemTypeId.FromCode("secrets.mapped-keys-required"),
                "Rotation must retain keys used by bindings",
                "secrets.mapped-keys-required")),
            HttpProblemMapping.For<SecretBindingLimitReached>(_ => new(
                409,
                PoleProblemTypeId.FromCode("secrets.binding-limit-reached"),
                "Secret binding limit reached",
                "secrets.binding-limit-reached")),
            HttpProblemMapping.For<SecretConsumerAlreadyBound>(_ => new(
                409,
                PoleProblemTypeId.FromCode("secrets.consumer-already-bound"),
                "Consumer already has a binding to this Secret",
                "secrets.consumer-already-bound")),
            HttpProblemMapping.For<SecretUnknownKeys>(_ => new(
                409,
                PoleProblemTypeId.FromCode("secrets.unknown-keys"),
                "Binding references unknown Secret keys",
                "secrets.unknown-keys")),
            HttpProblemMapping.For<SecretBindingsRemain>(_ => new(
                409,
                PoleProblemTypeId.FromCode("secrets.bindings-remain"),
                "Detach Secret bindings before deletion",
                "secrets.bindings-remain"))
        ]);
        return services;
    }

    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/secrets")
            .WithTags("Secrets")
            .WithMetadata(new TenantEndpointMetadata())
            .MapCreateSecretEndpoint()
            .MapListSecretsEndpoint()
            .MapGetSecretEndpoint()
            .MapRotateSecretEndpoint()
            .MapDeleteSecretEndpoint()
            .MapBindSecretEndpoint()
            .MapDetachSecretBindingEndpoint()
            .MapGetSecretUsageEndpoint();

        return app;
    }
}
