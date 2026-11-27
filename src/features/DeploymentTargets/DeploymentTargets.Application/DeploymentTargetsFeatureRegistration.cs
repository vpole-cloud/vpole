// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Contracts.HttpMapping;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.DeploymentTargets.Application;

public static class DeploymentTargetsFeatureRegistration
{
    public static IServiceCollection AddDeploymentTargetsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpProblemMappings(DeploymentTargetContractHttpMappings.All);
        services.TryAddSingleton(TimeProvider.System);
        services.AddDeploymentTargetPersistence();
        services.AddSingleton(new RealtimeTopic("deployment-targets", RealtimeScope.Platform));
        services.AddSingleton(new RealtimeTopic("deployment-target-eligibility", RealtimeScope.Tenant));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<DeploymentTargetOptions>, DeploymentTargetOptionsValidator>());
        services.AddOptions<DeploymentTargetOptions>()
            .Bind(configuration.GetSection(DeploymentTargetOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<DeploymentTargetOptions>>().Value);

        services.TryAddSingleton<DeploymentTargetConnectionProtector>();
        services.TryAddSingleton<DeploymentTargetConnectionPreparer>();
        services.TryAddSingleton<PrepareDeploymentTargetConnection>(provider => provider
            .GetRequiredService<DeploymentTargetConnectionPreparer>()
            .InvokeAsync);
        services.TryAddSingleton<DeploymentTargetKubernetesClientFactory>();
        services.TryAddSingleton<CreateDeploymentTargetKubernetesClient>(provider => provider
            .GetRequiredService<DeploymentTargetKubernetesClientFactory>()
            .InvokeAsync);
        services.TryAddSingleton<DeploymentTargetProbe>();
        services.TryAddSingleton<ProbeDeploymentTargetConnection>(provider => provider
            .GetRequiredService<DeploymentTargetProbe>()
            .InvokeAsync);
        services.TryAddScoped<DeploymentTargetValidationService>();

        // This is a genuine runtime protocol: consumers select it as a Kubernetes client
        // provider and it owns the lifecycle of cached, disposable clients.
        services.TryAddSingleton<KubernetesClientProvider>();
        services.TryAddSingleton<IKubernetesClientProvider>(provider =>
            provider.GetRequiredService<KubernetesClientProvider>());
        services.TryAddSingleton<InvalidateDeploymentTargetKubernetesClient>(provider =>
            provider.GetRequiredService<KubernetesClientProvider>().Invalidate);
        services.TryAddSingleton<DeploymentTargetBootstrapper>();
        services.TryAddSingleton<BootstrapDeploymentTargets>(provider => provider
            .GetRequiredService<DeploymentTargetBootstrapper>()
            .InvokeAsync);

        return services;
    }
}
