using Pole.Features.Outbox.Application.Persistence.Delivery;
using System.Reflection;
using Pole.ASP.Helpers;
using Pole.Features.Secrets.Application;
using Pole.Features.PrivateAccess.Application;
using Pole.Features.DeploymentTargets.Application.Persistence.Connectors;
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
    public static Assembly[] ApplicationAssemblies => [
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
    public static Assembly[] EventAssemblies => [
        typeof(Pole.Features.Secrets.Events.SecretChangedEvent).Assembly,
        typeof(Pole.Features.PrivateAccess.Events.PrivateAccessGrantCreatedEvent).Assembly,
    typeof(Pole.Features.ResourceDeployments.Events.ResourceDeploymentAppliedEvent).Assembly,
    typeof(Pole.Features.ResourceGroups.Events.ResourceGroupCreatedEvent).Assembly,
    typeof(Pole.Features.SourceBuilds.Events.SourceBuildRequestedEvent).Assembly,
    typeof(Pole.Features.Users.Events.UserCreatedEvent).Assembly
    ];

    public static IServiceCollection AddPolePlatform(this IServiceCollection services, IConfiguration configuration,
        Assembly[] additionalApplicationAssemblies, Assembly[] additionalEventAssemblies,
        Assembly[]? additionalMigrationAssemblies = null)
    {
        var applications = ApplicationAssemblies.Concat(additionalApplicationAssemblies).Distinct().ToArray();
        var events = EventAssemblies.Concat(additionalEventAssemblies).Distinct().ToArray();
        services.AddKubernetes(configuration).AddDatabase().AddOutboxFeature(configuration, events)
    .AddResourceGroupsFeature(configuration)
    .AddResourceDeploymentsFeature(configuration)
    .AddTenantsFeature()
    .AddDomainsFeature()
    .AddSecretsFeature()
    .AddPrivateAccessFeature()
    .AddPrivateAccessTargetAdmission()
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
            .AddMigrations(ApplicationAssemblies.Concat(additionalMigrationAssemblies ?? additionalApplicationAssemblies).Distinct().ToArray())
            .AddValidatorsFromAssemblies(applications)
            .AddPoleMediator(applications);
        return services;
    }

    public static async Task InitializePolePlatformAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _ = services.GetRequiredService<QuotaCatalog>();
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
