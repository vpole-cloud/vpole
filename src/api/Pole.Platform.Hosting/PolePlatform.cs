using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Events;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.ContainerRegistries.Application;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Migrations;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Events;
using Pole.Features.Domains.Migrations;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Events;
using Pole.Features.GitOps.Migrations;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Events;
using Pole.Features.GitProviders.Migrations;
using Pole.Features.Outbox.Application;
using Pole.Features.Outbox.Application.Delivery;
using Pole.Features.Outbox.Migrations;
using Pole.Features.PrivateAccess.Application;
using Pole.Features.PrivateAccess.Events;
using Pole.Features.PrivateAccess.Migrations;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Migrations;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Application.Redis;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceDeployments.Migrations;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.ResourceGroups.Migrations;
using Pole.Features.Search.Application;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Events;
using Pole.Features.Search.Migrations;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Events;
using Pole.Features.Secrets.Migrations;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Events;
using Pole.Features.SourceBuilds.Migrations;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Events;
using Pole.Features.StorageProfiles.Migrations;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Middleware;
using Pole.Features.Tenants.Events;
using Pole.Features.Users.Application;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Events;

namespace Pole.Platform.Hosting;

public static class PolePlatform
{
    public static Assembly[] ApplicationAssemblies =>
    [
        typeof(GitOpsFeature).Assembly,
        typeof(RealtimeFeature).Assembly,
        typeof(SecretFeature).Assembly,
        typeof(PrivateAccessFeature).Assembly,
        typeof(QuotaEndpoints).Assembly,
        typeof(DomainZoneEndpoints).Assembly,
        typeof(GitProviderEndpoints).Assembly,
        typeof(ContainerRegistryFeature).Assembly,
        typeof(ComputeProfileApplicationRegistration).Assembly,
        typeof(DeploymentTargetFeature).Assembly,
        typeof(SourceBuildFeature).Assembly,
        typeof(OutboxFeatureRegistration).Assembly,
        typeof(ResourceGroupEndpoints).Assembly,
        typeof(ResourceDeploymentFeature).Assembly,
        typeof(SearchEndpoints).Assembly,
        typeof(StorageProfileEndpoints).Assembly,
        typeof(TenantEndpoints).Assembly,
        typeof(UserEndpoints).Assembly,
    ];

    public static Assembly[] MigrationAssemblies =>
    [
        typeof(GitOpsIndexesMigration).Assembly,
        typeof(SecretIndexesMigration).Assembly,
        typeof(PrivateAccessIndexesMigration).Assembly,
        typeof(QuotaMigration).Assembly,
        typeof(DomainIndexesMigration).Assembly,
        typeof(GitProviderConnectionsMigration).Assembly,
        typeof(ContainerRegistryMigration).Assembly,
        typeof(ComputeProfileIndexesMigration).Assembly,
        typeof(DeploymentTargetMigration).Assembly,
        typeof(SourceBuildMigration).Assembly,
        typeof(OutboxMessageMigration).Assembly,
        typeof(ResourceGroupPlacementMigration).Assembly,
        typeof(ResourceDeploymentMigration).Assembly,
        typeof(SearchDocumentIndexesMigration).Assembly,
        typeof(StorageProfileMigration).Assembly,
    ];

    public static Assembly[] EventAssemblies =>
    [
        typeof(GitSourceWorkRequested).Assembly,
        typeof(DomainMetadataChangedEvent).Assembly,
        typeof(GitProviderConnectionChangedEvent).Assembly,
        typeof(ComputeProfileCatalogChangedEvent).Assembly,
        typeof(DeploymentTargetCatalogChangedEvent).Assembly,
        typeof(SearchProjectionChangedEvent).Assembly,
        typeof(StorageProfileCatalogChangedEvent).Assembly,
        typeof(TenantMembershipChangedEvent).Assembly,
        typeof(SecretChangedEvent).Assembly,
        typeof(PrivateAccessGrantCreatedEvent).Assembly,
        typeof(ResourceDeploymentAppliedEvent).Assembly,
        typeof(ResourceGroupCreatedEvent).Assembly,
        typeof(SourceBuildRequestedEvent).Assembly,
        typeof(UserCreatedEvent).Assembly,
    ];

    public static IServiceCollection AddPolePlatform(
        this IServiceCollection services,
        IConfiguration configuration,
        Assembly[] additionalApplicationAssemblies,
        Assembly[] additionalEventAssemblies,
        Assembly[]? additionalMigrationAssemblies = null,
        bool development = false)
    {
        var applications = ApplicationAssemblies.Concat(additionalApplicationAssemblies)
            .Distinct()
            .Where(assembly => configuration.GetValue<bool>("Realtime:Enabled") ||
                assembly != typeof(RealtimeFeature).Assembly)
            .ToArray();
        var events = EventAssemblies.Concat(additionalEventAssemblies).Distinct().ToArray();
        services.AddPoleHttpJson()
            .AddKubernetes(configuration)
            .AddDatabase()
            .AddRealtimeFeature(configuration, development)
            .AddRealtimeRedis(configuration, development)
            .AddOutboxFeature(configuration, events)
            .AddGitOpsFeature()
            .AddResourceGroupsFeature(configuration)
            .AddResourceDeploymentsFeature(configuration)
            .AddTenantsFeature()
            .AddDomainsFeature()
            .AddSecretsFeature()
            .AddPrivateAccessFeature()
            .AddComputeProfilesFeature()
            .AddStorageProfilesFeature()
            .AddQuotasFeature(configuration)
            .AddEnvelopeEncryption(configuration)
            .AddDeploymentTargetsFeature(configuration)
            .AddSearchFeature()
            .AddGitProvidersFeature(configuration)
            .AddContainerRegistriesFeature(configuration)
            .AddSourceBuildsFeature(configuration)
            .AddUsersFeature(configuration)
            .AddMigrations(MigrationAssemblies.Concat(additionalMigrationAssemblies ?? []).Distinct().ToArray())
            .AddValidatorsFromAssemblies(applications)
            .AddPoleMediator(applications);
        return services;
    }

    public static async Task InitializePolePlatformAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        _ = services.GetRequiredService<QuotaCatalog>();
        _ = services.GetRequiredService<RealtimeTopicRegistry>();
        _ = services.GetRequiredService<SearchSourceTypeRegistry>();
        services.ValidateResourceDeploymentContributions();
        services.ValidateSecretConsumerContributions();
        _ = services.GetRequiredService<IEnvelopeEncryptionService>();
        await services.GetRequiredService<EnsurePendingOutboxEventsRegistered>().InvokeAsync(cancellationToken);
        await services.GetRequiredService<MigrationRunner>().RunMigrations(cancellationToken);
    }

    public static WebApplication UsePoleTenantPipeline(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<TenantContextMiddleware>();
        return app;
    }
}
