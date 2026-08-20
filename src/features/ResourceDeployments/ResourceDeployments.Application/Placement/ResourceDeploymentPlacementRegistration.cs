using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Placement;

internal static class ResourceDeploymentPlacementRegistration
{
    internal static IServiceCollection AddResourceDeploymentPlacementCapabilities(
        this IServiceCollection services)
    {
        services.TryAddScoped<ResourceDeploymentPlacementResolver>();
        services.TryAddScoped<ResolveResourceDeploymentTargetLegacy>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentPlacementResolver>()
                .InvokeAsync);

        services.TryAddScoped<ResourceDeploymentWriter>();
        services.TryAddScoped<CreateResourceDeployment>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentWriter>()
                .InvokeAsync);
        services.TryAddScoped<ResourceDeploymentLegacyCreationAdapters>();
        services.TryAddScoped<CreateResourceDeploymentLegacy>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyCreationAdapters>()
                .CreateAsync);
        services.TryAddScoped<CreateResourceDeploymentSet>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentWriter>()
                .InvokeSetAsync);
        services.TryAddScoped<CreateResourceDeploymentSetLegacy>(serviceProvider =>
            serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyCreationAdapters>()
                .CreateSetAsync);
        services.TryAddScoped<ResourceDeploymentMutationWriter>();
        services.TryAddScoped<RequestResourceDeploymentApply>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>()
                .RequestApplyAsync);
        services.TryAddScoped<RequestResourceDeploymentDeletion>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>()
                .RequestDeletionAsync);
        services.TryAddScoped<RequestExactResourceDeploymentApply>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>()
                .RequestExactApplyAsync);
        services.TryAddScoped<RequestExactResourceDeploymentDeletion>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>()
                .RequestExactDeletionAsync);
        services.TryAddScoped<RequestResourceDeploymentSetApply>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>()
                .RequestSetApplyAsync);
        services.TryAddScoped<RequestResourceDeploymentSetDeletion>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>()
                .RequestSetDeletionAsync);
        services.TryAddScoped<AdvanceResourceDeploymentRevisionLegacy>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyMutationAdapters>()
                .AdvanceResourceRevisionAsync);
        services.TryAddScoped<AdvanceResourceDeploymentRevision>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>()
                .AdvanceResourceRevisionOutcomeAsync);
        services.TryAddScoped<RetryExactResourceDeployment>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentMutationWriter>()
                .RequestRetryAsync);
        services.TryAddScoped<ResourceDeploymentLegacyMutationAdapters>();
        services.TryAddScoped<RequestResourceDeploymentApplyLegacy>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyMutationAdapters>()
                .RequestApplyAsync);
        services.TryAddScoped<RequestResourceDeploymentDeletionLegacy>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyMutationAdapters>()
                .RequestDeletionAsync);
        services.TryAddScoped<RequestExactResourceDeploymentApplyLegacy>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyMutationAdapters>()
                .RequestExactApplyAsync);
        services.TryAddScoped<RequestExactResourceDeploymentDeletionLegacy>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyMutationAdapters>()
                .RequestExactDeletionAsync);
        services.TryAddScoped<RequestResourceDeploymentSetApplyLegacy>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyMutationAdapters>()
                .RequestSetApplyAsync);
        services.TryAddScoped<RequestResourceDeploymentSetDeletionLegacy>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyMutationAdapters>()
                .RequestSetDeletionAsync);
        services.TryAddScoped<RetryExactResourceDeploymentLegacy>(
            serviceProvider => serviceProvider
                .GetRequiredService<ResourceDeploymentLegacyMutationAdapters>()
                .RequestRetryAsync);

        services.TryAddScoped<ResourceDeploymentQueryService>();
        services.TryAddScoped<LoadResourceDeployment>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentQueryService>().GetAsync);
        services.TryAddScoped<LoadResourceDeployments>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentQueryService>().GetManyAsync);
        services.TryAddScoped<LoadResourceDeploymentSet>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentQueryService>().GetAllAsync);
        services.TryAddScoped<LoadExactResourceDeployment>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentQueryService>().GetExactAsync);
        services.TryAddScoped<LoadResourceDeploymentSets>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentQueryService>().GetAllManyAsync);

        services.TryAddScoped<ResourceDeploymentFinalizationService>();
        services.TryAddScoped<GetResourceDeploymentFinalizationReadiness>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentFinalizationService>().GetReadinessAsync);
        services.TryAddScoped<PurgeDeletedResourceDeployments>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentFinalizationService>().PurgeDeletedAsync);
        services.TryAddScoped<GetExactResourceDeploymentFinalizationReadiness>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentFinalizationService>().GetExactReadinessAsync);
        services.TryAddScoped<PurgeExactDeletedResourceDeployment>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentFinalizationService>().PurgeExactDeletedAsync);
        services.TryAddScoped<PurgeDeletedResourceDeploymentsLegacy>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentLegacyAdapters>().PurgeDeletedAsync);
        services.TryAddScoped<PurgeExactDeletedResourceDeploymentLegacy>(serviceProvider =>
            serviceProvider.GetRequiredService<ResourceDeploymentLegacyAdapters>().PurgeExactDeletedAsync);

        return services;
    }
}
