using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Security;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.DeploymentTargets.Application;

public static class DeploymentTargetsFeatureRegistration
{
    public static IServiceCollection AddDeploymentTargetsFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Application owns mappings for feature-local mutation outcomes;
        // stable contract mappings remain owned by their contract packages.
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpProblemMappings(DeploymentTargetHttpMappings.All);
        services.AddDeploymentTargetPersistence();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<DeploymentTargetOptions>,
                DeploymentTargetOptionsValidator>());
        services.AddOptions<DeploymentTargetOptions>()
            .Bind(configuration.GetSection(DeploymentTargetOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<DeploymentTargetOptions>>().Value);

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

        // Stateless admission depends only on the singleton Mongo context and
        // is also used by the singleton startup bootstrapper.
        services.TryAddSingleton<DeploymentTargetDomainAdmission>();
        services.TryAddSingleton<KubernetesClientProvider>();
        services.TryAddSingleton<IKubernetesClientProvider>(provider =>
            provider.GetRequiredService<KubernetesClientProvider>());
        services.TryAddSingleton<InvalidateDeploymentTargetKubernetesClient>(provider =>
            provider.GetRequiredService<KubernetesClientProvider>().Invalidate);
        services.TryAddSingleton<DeploymentTargetBootstrapper>();
        services.TryAddSingleton<BootstrapDeploymentTargets>(provider => provider
            .GetRequiredService<DeploymentTargetBootstrapper>()
            .InvokeAsync);

        services.TryAddScoped<DeploymentTargetAdmissionQueries>();
        services.TryAddScoped<LookupDeploymentTarget>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .LookupAsync);
        services.TryAddScoped<LookupDefaultDeploymentTarget>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .LookupDefaultAsync);
        services.TryAddScoped<ListEligibleDeploymentTargetsForNewWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .ListEligibleAsync);
        services.TryAddScoped<AdmitDeploymentTargetForWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .AdmitForWorkloadAsync);
        services.TryAddScoped<AdmitDefaultDeploymentTargetForNewWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .AdmitDefaultForNewWorkloadAsync);
        services.TryAddScoped<AdmitDeploymentTargetForNewWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .AdmitForWorkloadPlacementAsync);
        services.TryAddScoped<AdmitDeploymentTargetPublicIngressForNewWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .AdmitForNewWorkloadAsync);
        services.TryAddScoped<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .ListEligiblePublicIngressForNewWorkloadAsync);
        services.TryAddScoped<
            AdmitDeploymentTargetPublicIngressForDomainReservation>(
            provider => provider
                .GetRequiredService<DeploymentTargetAdmissionQueries>()
                .AdmitForDomainReservationAsync);
        services.TryAddScoped<DeploymentTargetReferenceAdmission>();
        services.TryAddScoped<ApplyDeploymentTargetReferenceChanges>(
            provider => provider
                .GetRequiredService<DeploymentTargetReferenceAdmission>()
                .ApplyChangesOutcomeAsync);
        services.TryAddScoped<FenceDeploymentTargetWorkloadConfigurations>(
            provider => provider
                .GetRequiredService<DeploymentTargetReferenceAdmission>()
                .FenceManyOutcomeAsync);
        services.TryAddScoped<MongoRewrapDeploymentTargetCredentials>();
        services.TryAddScoped<RewrapDeploymentTargetCredentials>(provider => provider
            .GetRequiredService<MongoRewrapDeploymentTargetCredentials>()
            .InvokeAsync);
        return services;
    }
}
