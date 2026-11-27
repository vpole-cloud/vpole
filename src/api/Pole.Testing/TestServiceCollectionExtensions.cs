// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Mediator;

namespace Pole.Testing;

public static class TestServiceCollectionExtensions
{
    public static IServiceCollection AddTestDeploymentTargets(this IServiceCollection services)
    {
        var deploymentTargetLookup = new TestDeploymentTargetLookup();
        services.AddSingleton(deploymentTargetLookup);
        services.AddSingleton<LookupDeploymentTarget>(deploymentTargetLookup.LookupAsync);
        services.AddSingleton<LookupDefaultDeploymentTarget>(deploymentTargetLookup.LookupDefaultAsync);
        services.AddSingleton<ListEligibleDeploymentTargetsForNewWorkload>(deploymentTargetLookup.ListEligibleAsync);
        services.AddSingleton<AdmitDefaultDeploymentTargetForNewWorkload>(
            deploymentTargetLookup.AdmitDefaultForNewWorkloadAsync);
        services.AddSingleton<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(
            deploymentTargetLookup.ListEligiblePublicIngressForNewWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetPublicIngressForNewWorkload>(
            deploymentTargetLookup.AdmitForNewWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetForWorkload>(deploymentTargetLookup.AdmitForWorkloadAsync);
        services.AddSingleton<AdmitDeploymentTargetForNewWorkload>(
            deploymentTargetLookup.AdmitForWorkloadPlacementAsync);
        services.AddSingleton<AdmitDeploymentTargetPublicIngressForDomainReservation>(
            deploymentTargetLookup.AdmitForDomainReservationAsync);
        services.AddSingleton<ApplyDeploymentTargetReferenceChanges>(deploymentTargetLookup.ApplyChangesOutcomeAsync);
        services.AddSingleton<FenceDeploymentTargetWorkloadConfigurations>(
            deploymentTargetLookup.FenceManyOutcomeAsync);
        return services;
    }

    /// <summary>
    ///     Call after feature registration in isolated hosts without WebServices; targets remain unavailable.
    /// </summary>
    public static IServiceCollection AddUnavailableSourceBuildTargets(this IServiceCollection services)
    {
        services.TryAddSingleton<SourceBuildWebServiceExists>((
            _, _,
            _) => Task.FromResult(false));
        services.TryAddSingleton<LoadSourceBuildTarget>((
            request, _,
            _) => Task.FromResult<LoadSourceBuildTargetOutcome>(
            new SourceBuildWebServiceNotFound(request.WebServiceId)));
        services.TryAddSingleton<AdmitSourceBuildWebService>((
            request, _,
            _) => Task.FromResult<AdmitSourceBuildWebServiceOutcome>(
            new SourceBuildWebServiceNotFound(request.WebServiceId)));
        services.TryAddSingleton<DeploySourceBuildWebService>((request, _) =>
            Task.FromResult<DeploySourceBuildWebServiceOutcome>(
                new SourceBuildWebServiceDeploymentError(new SourceBuildWebServiceNotFound(request.WebServiceId))));
        return services;
    }

    /// <summary>Use only for isolated handler tests. HTTP tests keep the production tenant pipeline.</summary>
    public static IServiceCollection ReplaceTenantAuthorizationWithFake(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>) &&
                         d.ImplementationType == typeof(TenantAuthorizationBehavior<,>))
                     .ToArray())
            services.Remove(descriptor);
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(FakeAuthBehaviour<,>));
        return services;
    }
}
