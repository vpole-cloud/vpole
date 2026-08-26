using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Harbor;
using Pole.Features.ContainerRegistries.Application.Persistence;
using Pole.Features.ContainerRegistries.Application.Persistence.Credentials;
using Pole.Features.ContainerRegistries.Application.Persistence.Projects;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;

namespace Pole.Features.ContainerRegistries.Application;

public static class ContainerRegistryApplicationRegistration
{
    public static IServiceCollection AddContainerRegistriesFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HarborOptions>(configuration.GetSection(HarborOptions.SectionName));
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<HarborOptions>>().Value);
        services.AddHttpProblemMappings(ContainerRegistryHttpMappings.All);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<RegistryProvisioningPersistence>();
        services.TryAddScoped<MongoLoadTenantHarborRegistryProject>();
        services.TryAddScoped<LoadTenantHarborRegistryProject>(provider =>
            provider.GetRequiredService<MongoLoadTenantHarborRegistryProject>().InvokeAsync);
        services.TryAddScoped<MongoLoadRegistryCredential>();
        services.TryAddScoped<LoadRegistryCredential>(provider =>
            provider.GetRequiredService<MongoLoadRegistryCredential>().InvokeAsync);
        services.TryAddScoped<RegistryCredentialRewrapPersistence>();

        services.TryAddSingleton<EnvelopeProtectRegistryCredentialSecret>();
        services.TryAddSingleton<ProtectRegistryCredentialSecret>(provider =>
            provider.GetRequiredService<EnvelopeProtectRegistryCredentialSecret>().InvokeAsync);
        services.TryAddSingleton<EnvelopeUnprotectRegistryCredentialSecret>();
        services.TryAddSingleton<UnprotectRegistryCredentialSecret>(provider =>
            provider.GetRequiredService<EnvelopeUnprotectRegistryCredentialSecret>().InvokeAsync);
        services.TryAddScoped<RegistryCredentialResolver>();
        services.TryAddScoped<ResolveRegistryCredential>(provider =>
            provider.GetRequiredService<RegistryCredentialResolver>().InvokeAsync);
        services.TryAddScoped<RegistryCredentialRewrapService>();
        services.TryAddScoped<RewrapAllRegistryCredentials>(provider =>
            provider.GetRequiredService<RegistryCredentialRewrapService>().InvokeAsync);

        services.TryAddScoped<RegistryProvisioningCleanupService>();
        services.TryAddScoped<ContainerRegistryProvisioningService>();
        services.TryAddScoped<EnsureTenantContainerRegistryProject>(provider =>
            provider.GetRequiredService<ContainerRegistryProvisioningService>().InvokeAsync);
        services.TryAddScoped<ContainerRegistryBuildPushPreparer>();
        services.TryAddScoped<PrepareRegistryBuildPush>(provider =>
            provider.GetRequiredService<ContainerRegistryBuildPushPreparer>().InvokeAsync);
        services.TryAddScoped<TenantRegistryArtifactLookup>();
        services.TryAddScoped<GetTenantRegistryArtifact>(provider =>
            provider.GetRequiredService<TenantRegistryArtifactLookup>().InvokeAsync);
        services.TryAddScoped<RuntimeRegistryAccessProvider>();
        services.TryAddScoped<ResolveRuntimeRegistryAccess>(provider =>
            provider.GetRequiredService<RuntimeRegistryAccessProvider>().ResolveAsync);

        services.AddHttpClient<IContainerRegistryClient, HarborContainerRegistryClient>(client =>
            client.Timeout = TimeSpan.FromMinutes(2))
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                // Harbor's Basic-auth API can set a browser session cookie. Replaying it without
                // its corresponding CSRF context makes subsequent mutating requests fail with 403.
                UseCookies = false
            });
        return services;
    }
}
