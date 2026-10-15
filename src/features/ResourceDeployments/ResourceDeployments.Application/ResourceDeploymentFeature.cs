using Microsoft.Extensions.Configuration;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Diagnostics;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Application.Realtime;
using Pole.Features.ResourceDeployments.Application.Persistence.Observation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Contracts.HttpMapping;

namespace Pole.Features.ResourceDeployments.Application;

public static class ResourceDeploymentFeature
{
    public static IServiceCollection AddResourceDeploymentsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(new RealtimeTopic("operations", RealtimeScope.Tenant));
        services.AddSingleton(new RealtimeTopic("runtime-observations", RealtimeScope.Tenant));
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
        services.TryAddScoped<ResourceDeploymentOperationsPersistence>();
        services.TryAddSingleton<ResourceDeploymentObserverCatalog>();
        services.TryAddSingleton<IResourceDeploymentObservationReadBudget, ResourceDeploymentObservationReadBudget>();
        services.AddOptions<ResourceDeploymentObservationOptions>()
            .Bind(configuration.GetSection(ResourceDeploymentObservationOptions.SectionName))
            .Configure(options => options.Enabled = options.Enabled && configuration.GetValue<bool>("Realtime:Enabled"))
            .Validate(options => options.Valid, "Runtime observation limits are outside the supported bounds.")
            .ValidateOnStart();
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<ResourceDeploymentObservationOptions>>().Value);
        services.TryAddScoped<IResourceDeploymentObservationStore, MongoResourceDeploymentObservationStore>();
        services.TryAddScoped<ResourceDeploymentObservationContextLoader>();
        services.TryAddSingleton<ResourceDeploymentObservationMetrics>();
        services.TryAddScoped<ResourceDeploymentSharedObservationReader>();
        services.TryAddSingleton<ResourceDeploymentObservationWakeup>();
        services.TryAddEnumerable(ServiceDescriptor
            .Scoped<IRealtimeTopicLifecycle, ResourceDeploymentObservationLifecycle>());
        services.AddHostedService<ResourceDeploymentObservationCoordinator>();
        services.TryAddScoped<ObserveResourceDeployment>();
        services.AddOptions<RuntimeDiagnosticsOptions>()
            .Bind(configuration.GetSection(RuntimeDiagnosticsOptions.SectionName))
            .Validate(options => options.Valid, "RuntimeDiagnostics limits are outside the supported bounds.")
            .ValidateOnStart();
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<RuntimeDiagnosticsOptions>>().Value);
        services.TryAddSingleton<RuntimeLogCapacity>();
        services.TryAddScoped<ResourceDeploymentDiagnostics>();
        services.TryAddSingleton<ResourceIngressPolicyCatalog>();
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

    public static void ValidateResourceDeploymentContributions(this IServiceProvider services)
    {
        _ = services.GetRequiredService<ResourceDeploymentAdapterCatalog>();
        var observers = services.GetRequiredService<ResourceDeploymentObserverCatalog>();
        var registrations = services.GetRequiredService<IServiceProviderIsService>();
        foreach (var descriptor in observers.Contributions)
            foreach (var type in new[]
                     {
                     descriptor.ObserverType, descriptor.DiagnosticsType, descriptor.ChangeSourceType,
                     descriptor.BoundObserverType
                 }.OfType<Type>())
                if (!registrations.IsService(type))
                    throw new InvalidOperationException("A resource deployment observer contribution is not registered.");
        _ = services.GetRequiredService<ResourceIngressPolicyCatalog>();
    }

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
