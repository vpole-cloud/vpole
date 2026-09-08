using Microsoft.Extensions.DependencyInjection;
using Pole.Mediator;
using Pole.Features.DeploymentTargets.Contracts;
namespace Pole.Testing;

public static class TestServiceCollectionExtensions
{
    public static IServiceCollection AddTestDeploymentTargets(this IServiceCollection services)
    {
        var deploymentTargetLookup = new TestDeploymentTargetLookup();
        services.AddSingleton(deploymentTargetLookup);
        services.AddSingleton<LookupDeploymentTarget>(
            deploymentTargetLookup.LookupAsync);
        services.AddSingleton<LookupDefaultDeploymentTarget>(
            deploymentTargetLookup.LookupDefaultAsync);
        services.AddSingleton<ListEligibleDeploymentTargetsForNewWorkload>(
            deploymentTargetLookup.ListEligibleAsync);
        services.AddSingleton<AdmitDefaultDeploymentTargetForNewWorkload>(
            deploymentTargetLookup.AdmitDefaultForNewWorkloadAsync);
        services.AddSingleton<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(
            deploymentTargetLookup.ListEligiblePublicIngressForNewWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetPublicIngressForNewWorkload>(
            deploymentTargetLookup.AdmitForNewWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetForWorkload>(
            deploymentTargetLookup.AdmitForWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetForNewWorkload>(
            deploymentTargetLookup.AdmitForWorkloadPlacementAsync);
        services.AddSingleton<AdmitDeploymentTargetPublicIngressForDomainReservation>(
            deploymentTargetLookup.AdmitForDomainReservationAsync);
        services.AddSingleton<ApplyDeploymentTargetReferenceChanges>(
            deploymentTargetLookup.ApplyChangesOutcomeAsync);
        services.AddSingleton<FenceDeploymentTargetWorkloadConfigurations>(
            deploymentTargetLookup.FenceManyOutcomeAsync);
        return services;
    }

    /// <summary>Use only for isolated handler tests. HTTP tests keep the production tenant pipeline.</summary>
    public static IServiceCollection ReplaceTenantAuthorizationWithFake(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>) &&
                     d.ImplementationType == typeof(TenantAuthorizationBehavior<,>)).ToArray())
            services.Remove(descriptor);
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FakeAuthBehaviour<,>));
        return services;
    }
}
