using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
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
        // The host composes mappings from the sibling Contracts.HttpMapping
        // project so Application stays independent of HTTP transport adapters.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<DeploymentTargetOptions>,
                DeploymentTargetOptionsValidator>());
        services.AddOptions<DeploymentTargetOptions>()
            .Bind(configuration.GetSection(DeploymentTargetOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<DeploymentTargetOptions>>().Value);

        services.TryAddSingleton<
            IDeploymentTargetConnectionProtector,
            DeploymentTargetConnectionProtector>();
        services.TryAddSingleton<DeploymentTargetConnectionService>();
        services.TryAddSingleton<IDeploymentTargetConnectionPreparer>(provider =>
            provider.GetRequiredService<DeploymentTargetConnectionService>());
        services.TryAddSingleton<IDeploymentTargetKubernetesClientFactory>(provider =>
            provider.GetRequiredService<DeploymentTargetConnectionService>());
        services.TryAddSingleton<IDeploymentTargetProbe, DeploymentTargetProbe>();
        services.TryAddScoped<DeploymentTargetValidationService>();

        // Stateless admission depends only on the singleton Mongo context and
        // is also used by the singleton startup bootstrapper.
        services.TryAddSingleton<DeploymentTargetDomainAdmission>();
        services.TryAddSingleton<KubernetesClientProvider>();
        services.TryAddSingleton<IKubernetesClientProvider>(provider =>
            provider.GetRequiredService<KubernetesClientProvider>());
        services.TryAddSingleton<IDeploymentTargetClientCache>(provider =>
            provider.GetRequiredService<KubernetesClientProvider>());
        services.TryAddSingleton<
            IDeploymentTargetBootstrapper,
            DeploymentTargetBootstrapper>();

        services.TryAddScoped<DeploymentTargetLookup>();
        services.TryAddScoped<AdmitDeploymentTargetForWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetLookup>()
                .AdmitForWorkloadAsync);
        services.TryAddScoped<AdmitDeploymentTargetPublicIngressForNewWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetLookup>()
                .AdmitForNewWorkloadAsync);
        services.TryAddScoped<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(
            provider => provider
                .GetRequiredService<DeploymentTargetLookup>()
                .ListEligiblePublicIngressForNewWorkloadAsync);
        services.TryAddScoped<
            AdmitDeploymentTargetPublicIngressForDomainReservation>(
            provider => provider
                .GetRequiredService<DeploymentTargetLookup>()
                .AdmitForDomainReservationAsync);
        services.TryAddScoped<IDeploymentTargetLookup>(provider =>
            provider.GetRequiredService<DeploymentTargetLookup>());
        services.TryAddScoped<DeploymentTargetReferenceAdmission>();
        services.TryAddScoped<ApplyDeploymentTargetReferenceChanges>(
            provider => provider
                .GetRequiredService<DeploymentTargetReferenceAdmission>()
                .ApplyChangesOutcomeAsync);
        services.TryAddScoped<IDeploymentTargetReferenceAdmission>(provider =>
            provider.GetRequiredService<DeploymentTargetReferenceAdmission>());
        services.TryAddScoped<IDeploymentTargetWorkloadConfigurationFence>(provider =>
            provider.GetRequiredService<DeploymentTargetReferenceAdmission>());
        services.TryAddScoped<
            IDeploymentTargetDeletionGuard,
            DeploymentTargetDeletionGuard>();
        services.TryAddScoped<DeploymentTargetReader>();
        services.TryAddScoped<DeploymentTargetPersistence>();
        services.TryAddScoped<
            IDeploymentTargetCredentialRewrapService,
            DeploymentTargetCredentialRewrapService>();
        return services;
    }
}
