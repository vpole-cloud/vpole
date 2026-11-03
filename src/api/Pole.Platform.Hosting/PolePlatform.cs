using Pole.Features.GitOps.Application;
using Pole.Features.Outbox.Application.Persistence.Delivery;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Application.Redis;
using System.Reflection;
using Pole.ASP.Helpers;
using Pole.Features.Secrets.Application;
using Pole.Features.PrivateAccess.Application;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Bootstrap;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.Outbox.Application;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Middleware;
using Pole.Features.Users.Application;
using Pole.Features.Users.Application.Common;
using Pole.Observability;
using Pole.Platform.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        typeof(Pole.Features.GitOps.Migrations.GitOpsIndexesMigration).Assembly,
        typeof(Pole.Features.Secrets.Migrations.SecretIndexesMigration).Assembly,
        typeof(Pole.Features.PrivateAccess.Migrations.PrivateAccessIndexesMigration).Assembly,
        typeof(Pole.Features.Quotas.Migrations.QuotaMigration).Assembly,
        typeof(Pole.Features.Domains.Migrations.DomainIndexesMigration).Assembly,
        typeof(Pole.Features.GitProviders.Migrations.GitProviderConnectionsMigration).Assembly,
        typeof(Pole.Features.ContainerRegistries.Migrations.ContainerRegistryMigration).Assembly,
        typeof(Pole.Features.ComputeProfiles.Migrations.ComputeProfileIndexesMigration).Assembly,
        typeof(Pole.Features.DeploymentTargets.Migrations.DeploymentTargetMigration).Assembly,
        typeof(Pole.Features.SourceBuilds.Migrations.SourceBuildMigration).Assembly,
        typeof(Pole.Features.Outbox.Migrations.OutboxMessageMigration).Assembly,
        typeof(Pole.Features.ResourceGroups.Migrations.ResourceGroupPlacementMigration).Assembly,
        typeof(Pole.Features.ResourceDeployments.Migrations.ResourceDeploymentMigration).Assembly,
        typeof(Pole.Features.Search.Migrations.SearchDocumentIndexesMigration).Assembly,
        typeof(Pole.Features.StorageProfiles.Migrations.StorageProfileMigration).Assembly
    ];

    public static Assembly[] EventAssemblies =>
    [
        typeof(Pole.Features.GitOps.Events.GitSourceWorkRequested).Assembly,
        typeof(Pole.Features.Domains.Events.DomainMetadataChangedEvent).Assembly,
        typeof(Pole.Features.GitProviders.Events.GitProviderConnectionChangedEvent).Assembly,
        typeof(Pole.Features.ComputeProfiles.Events.ComputeProfileCatalogChangedEvent).Assembly,
        typeof(Pole.Features.DeploymentTargets.Events.DeploymentTargetCatalogChangedEvent).Assembly,
        typeof(Pole.Features.Search.Events.SearchProjectionChangedEvent).Assembly,
        typeof(Pole.Features.StorageProfiles.Events.StorageProfileCatalogChangedEvent).Assembly,
        typeof(Pole.Features.Tenants.Events.TenantMembershipChangedEvent).Assembly,
        typeof(Pole.Features.Secrets.Events.SecretChangedEvent).Assembly,
        typeof(Pole.Features.PrivateAccess.Events.PrivateAccessGrantCreatedEvent).Assembly,
        typeof(Pole.Features.ResourceDeployments.Events.ResourceDeploymentAppliedEvent).Assembly,
        typeof(Pole.Features.ResourceGroups.Events.ResourceGroupCreatedEvent).Assembly,
        typeof(Pole.Features.SourceBuilds.Events.SourceBuildRequestedEvent).Assembly,
        typeof(Pole.Features.Users.Events.UserCreatedEvent).Assembly
    ];

    public static IServiceCollection AddPolePlatform(this IServiceCollection services,
        IConfiguration configuration,
        Assembly[] additionalApplicationAssemblies,
        Assembly[] additionalEventAssemblies,
        Assembly[]? additionalMigrationAssemblies = null,
        bool development = false)
    {
        var applications = ApplicationAssemblies.Concat(additionalApplicationAssemblies).Distinct()
            .Where(assembly => configuration.GetValue<bool>("Realtime:Enabled") ||
                               assembly != typeof(RealtimeFeature).Assembly).ToArray();
        var events = EventAssemblies.Concat(additionalEventAssemblies).Distinct().ToArray();
        services.AddKubernetes(configuration).AddDatabase()
            .AddRealtimeFeature(configuration, development).AddRealtimeRedis(configuration, development)
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
            .AddMigrations(MigrationAssemblies
                .Concat(additionalMigrationAssemblies ?? []).Distinct().ToArray())
            .AddValidatorsFromAssemblies(applications)
            .AddPoleMediator(applications);
        return services;
    }

    public static async Task InitializePolePlatformAsync(this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        _ = services.GetRequiredService<QuotaCatalog>();
        _ = services.GetRequiredService<RealtimeTopicRegistry>();
        _ = services.GetRequiredService<SearchSourceTypeRegistry>();
        services.ValidateResourceDeploymentContributions();
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
