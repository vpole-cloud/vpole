// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Credentials;
using Pole.Features.ContainerRegistries.Application.Harbor;
using Pole.Features.ContainerRegistries.Application.Projects;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application;

public static class ContainerRegistryApplicationRegistration
{
    public static IServiceCollection AddContainerRegistriesFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HarborOptions>(configuration.GetSection(HarborOptions.SectionName));
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<HarborOptions>>().Value);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<RegistryProject>("RegistryProjects"));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<RegistryCredential>("RegistryCredentials"));

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
                UseCookies = false,
            });
        return services;
    }

    public static IServiceProvider ScheduleContainerRegistryRecurringJobs(this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<RegistryProvisioningCleanupService>(
            "cleanup-container-registry-provisioning",
            job => job.ReconcileAsync(CancellationToken.None),
            Cron.Minutely);
        return services;
    }
}
