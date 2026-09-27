using Microsoft.Extensions.Configuration;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Contracts.HttpMapping;

namespace Pole.Features.ResourceDeployments.Application;

public static class ResourceDeploymentFeature
{
    public static IServiceCollection AddResourceDeploymentsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpProblemMappings(ResourceDeploymentContractHttpMappings.All);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<ResourceDeploymentSettings>,
                ResourceDeploymentSettingsValidator>());
        services.AddOptions<ResourceDeploymentSettings>()
            .Bind(configuration.GetSection(ResourceDeploymentSettings.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(serviceProvider =>
            serviceProvider
                .GetRequiredService<IOptions<ResourceDeploymentSettings>>()
                .Value);

        services.AddResourceDeploymentPersistence();
        services.TryAddScoped<KubernetesResourceIngressPolicyManager>();
        services.TryAddScoped<ReconcileResourceIngressPolicy>(serviceProvider =>
            serviceProvider
                .GetRequiredService<KubernetesResourceIngressPolicyManager>()
                .InvokeAsync);

        services.TryAddSingleton<ResourceDeploymentMetrics>();
        services.TryAddSingleton<ResourceDeploymentAdapterCatalog>();
        services.TryAddScoped<ResourceDeploymentAdapterResolver>();
        services.TryAddScoped<ResolveResourceDeploymentAdapter>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentAdapterResolver>()
                .Resolve);
        services.TryAddSingleton<ResourceDeploymentRetryDelayPolicy>();
        services.TryAddScoped<ResourceDeploymentReconciler>();
        services.TryAddScoped<ResourceDeploymentRecoveryJob>();

        return services;
    }

    public static void ValidateResourceDeploymentContributions(this IServiceProvider services) =>
        _ = services.GetRequiredService<ResourceDeploymentAdapterCatalog>();

    public static IServiceProvider ScheduleResourceDeploymentRecurringJobs(
        this IServiceProvider services)
    {
        RecurringJob.AddOrUpdate<ResourceDeploymentRecoveryJob>(
            "recover-resource-deployments",
            job => job.RecoverAsync(CancellationToken.None),
            Cron.Minutely);
        return services;
    }

}
