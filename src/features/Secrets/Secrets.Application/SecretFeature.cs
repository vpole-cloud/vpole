using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Secrets.Application.Endpoints;

namespace Pole.Features.Secrets.Application;

public static class SecretFeature
{
    public static IServiceCollection AddSecretsFeature(this IServiceCollection services)
    {
        services.AddSecretPersistence();
        services.AddHttpProblemMappings([HttpProblemMapping.For<SecretError>(MapError)]);
        return services;
    }

    private static HttpProblem MapError(SecretError error) => error switch
    {
        SecretMissing => new(
            404,
            PoleProblemTypeId.FromCode("secrets.not-found"),
            "Secret not found",
            "secrets.not-found"),
        SecretConflict => new(
            409,
            PoleProblemTypeId.FromCode("secrets.conflict"),
            "Secret version, keys, or bindings conflict",
            "secrets.conflict"),
        SecretReferenceRejected => new(
            422,
            PoleProblemTypeId.FromCode("secrets.reference-rejected"),
            "Secret reference is unavailable or outside its scope",
            "secrets.reference-rejected"),
        SecretEncryptionFailed => new(
            503,
            PoleProblemTypeId.FromCode("secrets.encryption-unavailable"),
            "Secret encryption unavailable",
            "secrets.encryption-unavailable")
    };

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

public sealed class RemoveSecretBindingsOnResourceUnregistered(SecretStore store)
    : IConsumer<ResourceUnregisteredEvent>
{
    public Task Handle(
        ResourceUnregisteredEvent message,
        CancellationToken cancellationToken = default) =>
        store.CleanupConsumerAsync(
            message.EventCore.TenantId,
            message.ResourceGroupId,
            message.ResourceType,
            message.ResourceId,
            cancellationToken);

    public Task Consume(ConsumeContext<ResourceUnregisteredEvent> context) =>
        Handle(context.Message, context.CancellationToken);
}
