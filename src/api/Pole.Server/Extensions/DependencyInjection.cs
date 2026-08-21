using System.Reflection;
using FluentValidation;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Application.Harbor;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Application.Security;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Contracts.HttpMapping;
using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.Functions.Runtime.Security;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.Features.Functions.Runtime.Scaling;
using Pole.Features.Functions.Runtime.Timers;
using Pole.Features.Functions.Runtime.Metering;
using Pole.Features.Outbox.Application;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application.Services;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Application.Placement;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Jobs;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Search.Application.Common;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.Tenants;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Application.Identity;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Mediator;
using Pole.Observability;
using Pole.Server.Database;

namespace Pole.Server.Extensions;

public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPoleMediator(params Assembly[] assemblies)
        {
            services.AddHttpProblemMappings(MediatorHttpProblemMappings.All);
            services.AddHttpProblemMappings(
                TenantAuthorizationHttpProblemMappings.All);
            services.AddHttpProblemMappings(
                TenantMemberAuthorizationHttpProblemMappings.All);
            services.AddHttpProblemMappings(
                DomainContractHttpMappings.All);
            services.AddScoped<IMediator, Pole.Mediator.Mediator>();
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantAuthorizationBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantMemberAuthorizationBehavior<,>));

            foreach (var assembly in assemblies)
            {
                var handlerTypes = assembly.GetTypes()
                    .Where(t => t is { IsClass: true, IsAbstract: false } &&
                                t.GetInterfaces().Any(IsMediatorHandler));

                foreach (var handlerType in handlerTypes)
                {
                    foreach (var interfaceType in handlerType.GetInterfaces()
                                 .Where(IsMediatorHandler))
                    {
                        services.AddScoped(interfaceType, handlerType);
                    }
                }
            }

            return services;

            static bool IsMediatorHandler(Type interfaceType) =>
                interfaceType.IsGenericType &&
                interfaceType.GetGenericTypeDefinition() is var definition &&
                (definition == typeof(IMediatorHandler<,>) ||
                 definition == typeof(IMediatorOutcomeHandler<,>));
        }
        
        public IServiceCollection AddValidatorsFromAssemblies(params Assembly[] assemblies)
        {
            foreach (var assembly in assemblies)
            {
                services.AddValidatorsFromAssembly(assembly);
            }
            return services;
        }

        public IServiceCollection AddMigrations(params Assembly[] assemblies)
        {
            var migrationTypes = assemblies
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => type is { IsClass: true, IsAbstract: false } &&
                               typeof(IMigration).IsAssignableFrom(type));

            foreach (var migrationType in migrationTypes)
            {
                services.AddSingleton(typeof(IMigration), migrationType);
            }

            return services;
        }

        public IServiceCollection AddKubernetes(IConfiguration configuration)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IValidateOptions<FunctionGatewayOptions>, FunctionGatewayOptionsValidator>();
            services.AddOptions<FunctionGatewayOptions>()
                .Bind(configuration.GetSection(FunctionGatewayOptions.SectionName))
                .ValidateOnStart();
            services.AddSingleton(provider =>
                provider.GetRequiredService<IOptions<FunctionGatewayOptions>>().Value);
            services.AddSingleton<IValidateOptions<FunctionCoordinationOptions>, FunctionCoordinationOptionsValidator>();
            services.AddOptions<FunctionCoordinationOptions>()
                .Bind(configuration.GetSection(FunctionCoordinationOptions.SectionName))
                .ValidateOnStart();
            services.AddSingleton(provider =>
                provider.GetRequiredService<IOptions<FunctionCoordinationOptions>>().Value);
            services.AddSingleton<IValidateOptions<FunctionScalerOptions>, FunctionScalerOptionsValidator>();
            services.AddOptions<FunctionScalerOptions>()
                .Bind(configuration.GetSection(FunctionScalerOptions.SectionName))
                .ValidateOnStart();
            services.AddSingleton(provider =>
                provider.GetRequiredService<IOptions<FunctionScalerOptions>>().Value);
            services.AddSingleton<IValidateOptions<FunctionTimerOptions>, FunctionTimerOptionsValidator>();
            services.AddOptions<FunctionTimerOptions>()
                .Bind(configuration.GetSection(FunctionTimerOptions.SectionName))
                .ValidateOnStart();
            services.AddSingleton(provider =>
                provider.GetRequiredService<IOptions<FunctionTimerOptions>>().Value);
            var functionAgentTokenSection = configuration.GetSection(FunctionAgentTokenOptions.SectionName);
            if (functionAgentTokenSection.Exists())
            {
                var tokenOptions = functionAgentTokenSection.Get<FunctionAgentTokenOptions>() ??
                                   throw new InvalidOperationException("Function Agent token configuration is invalid.");
                tokenOptions.Validate();
                services.AddSingleton(tokenOptions);
                services.AddSingleton<IFunctionAgentSigningKeyProvider, FileFunctionAgentSigningKeyProvider>();
                services.AddSingleton<FunctionAgentTokenIssuer>();
                services.AddSingleton<IFunctionAgentTokenIssuer>(provider =>
                    provider.GetRequiredService<FunctionAgentTokenIssuer>());
                services.AddSingleton<IFunctionAgentTrustBundleProvider>(provider =>
                    provider.GetRequiredService<FunctionAgentTokenIssuer>());
                services.AddHttpClient<IFunctionAgentClient, FunctionAgentClient>(client =>
                    client.Timeout = Timeout.InfiniteTimeSpan)
                    .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
                    {
                        AllowAutoRedirect = false,
                        ConnectTimeout = TimeSpan.FromSeconds(5)
                    });
                services.AddScoped<IFunctionRevisionProvisioner, FunctionRevisionProvisioner>();
                services.TryAddSingleton<IFunctionEndpointSelector, FunctionEndpointSelector>();
                services.TryAddSingleton<FunctionCoordinationClient>();
                services.TryAddSingleton<IFunctionAdmissionCoordinator>(provider =>
                    provider.GetRequiredService<FunctionCoordinationClient>());
                services.TryAddSingleton<IFunctionTrafficCoordinator>(provider =>
                    provider.GetRequiredService<FunctionCoordinationClient>());
                services.TryAddSingleton<IFunctionActivationCoordinator>(provider =>
                    provider.GetRequiredService<FunctionCoordinationClient>());
                services.TryAddSingleton<IFunctionScalingCoordinator>(provider =>
                    provider.GetRequiredService<FunctionCoordinationClient>());
                services.TryAddSingleton<IFunctionPendingAdmissionLimiter, FunctionPendingAdmissionLimiter>();
                services.TryAddSingleton<IFunctionPublicRateLimiter, FunctionPublicRateLimiter>();
                services.TryAddSingleton<IFunctionMeterWriter, FunctionMeterWriter>();
                services.TryAddSingleton<IFunctionHttpGateway, FunctionHttpGateway>();
                services.TryAddSingleton<IFunctionScaleTargetSource, MongoFunctionScaleTargetSource>();
                services.TryAddSingleton<IFunctionScaler, FunctionScaler>();
                services.AddHostedService<FunctionScalerWorker>();
                services.TryAddSingleton<IFunctionTimerScheduler, FunctionTimerScheduler>();
                services.AddHostedService<FunctionTimerSchedulerWorker>();
                services.TryAddSingleton<IFunctionInvocationDispatcher, FunctionInvocationDispatcher>();
                services.AddHostedService<FunctionInvocationDispatcherWorker>();
                services.TryAddSingleton<IFunctionUsageAggregator, FunctionUsageAggregator>();
                services.AddHostedService<FunctionUsageAggregatorWorker>();
            }
            services.TryAddSingleton<IKubernetesNamespaceGenerator, KubernetesNamespaceGenerator>();
            services.TryAddScoped<
                IPostgresCredentialService,
                PostgresCredentialService>();
            services.TryAddScoped<
                IPostgresResourceDeploymentReader,
                MongoPostgresResourceDeploymentReader>();
            services.TryAddScoped<PostgresResourceDeploymentAdapter>();
            services.TryAddSingleton<
                IPostgresClusterDeploymentManager,
                PostgresClusterDeploymentManager>();
            services.TryAddScoped<WebServiceResourceDeploymentAdapter>();
            services.TryAddScoped<RedisResourceDeploymentAdapter>();
            services.TryAddSingleton<IFunctionRevisionKubernetesManager, FunctionRevisionKubernetesManager>();
            services.TryAddSingleton<IFunctionEndpointSliceSource, KubernetesFunctionEndpointSliceSource>();
            services.TryAddSingleton<IFunctionEndpointRegistry, FunctionEndpointRegistry>();
            services.TryAddSingleton<IFunctionRevisionActivator, FunctionRevisionActivator>();
            services.AddHostedService<FunctionEndpointSliceWatcher>();
            return services;
        }

        public IServiceCollection AddEventBus(IConfiguration configuration, Assembly[] assemblies)
        {
            services.AddMassTransit(configurator =>
            {
                configurator.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(configuration.GetConnectionString("pole-rabbitmq"));
                    cfg.UsePublishFilter(typeof(TraceContextPublishFilter<>), context);
                    cfg.UseConsumeFilter(typeof(TraceContextConsumeFilter<>), context);
                    cfg.ConfigureEndpoints(context);
                });
                configurator.AddConsumers(assemblies);
            });
            return services;
        }
        
        public IServiceCollection AddDatabase()
        {
            services.AddSingleton<IMongoDatabase>(sp =>
                sp.GetRequiredService<IMongoClient>().GetDatabase("pole-database"));
            services.AddMongoDbContextInterfaces();
            services.AddSingleton<MigrationRunner>();
            return services;
        }

        public IServiceCollection AddMongoDbContextInterfaces()
        {
            services.TryAddSingleton<MongoDbContext>();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<IMongoDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IEventDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton(serviceProvider =>
                serviceProvider
                    .GetRequiredService<IMongoDatabase>()
                    .GetCollection<OutboxMessage>("OutboxMessages"));
            services.TryAddSingleton<MongoStoreOutboxWrites>();
            services.TryAddSingleton<StoreOutboxWrites>(serviceProvider =>
                serviceProvider
                    .GetRequiredService<MongoStoreOutboxWrites>()
                    .InvokeAsync);
            services.TryAddSingleton<MongoRunTransaction>();
            services.TryAddSingleton<RunMongoTransaction>(serviceProvider =>
                serviceProvider
                    .GetRequiredService<MongoRunTransaction>()
                    .InvokeAsync);
            services.TryAddSingleton<ISearchDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IPostgresDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IQuotaDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IResourceGroupDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<ITenantDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IUserDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IStorageProfileDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IGitProviderDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IContainerRegistryDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IDeploymentTargetDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<ISourceBuildDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.TryAddSingleton<IFunctionAppDbContext>(sp => sp.GetRequiredService<MongoDbContext>());
            services.AddHttpContextAccessor();
            services.TryAddScoped<ResourceGroupLookup>();
            services.TryAddScoped<IResourceGroupLookup>(provider =>
                provider.GetRequiredService<ResourceGroupLookup>());
            services.TryAddScoped<IResourceGroupOwnershipLookup>(provider =>
                provider.GetRequiredService<ResourceGroupLookup>());
            services.TryAddScoped<IResourcePlacementService, ResourcePlacementService>();
            services.TryAddScoped<IResourcePlacementLookup, ResourcePlacementLookup>();
            services.TryAddScoped<
                IStorageProfileResolutionFence,
                StorageProfileResolutionFence>();
            services.AddSingleton(
                WebServiceResourceDeploymentAdapterRegistration.Descriptor);
            services.AddSingleton(
                RedisResourceDeploymentAdapterRegistration.Descriptor);
            services.AddSingleton(
                PostgresResourceDeploymentAdapterRegistration.Descriptor);
            services.TryAddScoped<IResourceGroupDeletionFinalizer, ResourceGroupDeletionFinalizer>();
            services.TryAddSingleton<IRuntimeCellNamespaceNameGenerator, RuntimeCellNamespaceNameGenerator>();
            services.TryAddScoped<IRuntimeCellNamespaceManager, KubernetesRuntimeCellNamespaceManager>();
            services.TryAddScoped<RuntimeCellRecoveryJob>();
            services.TryAddScoped<IFunctionRevisionLifecycleService, FunctionRevisionLifecycleService>();
            services.TryAddSingleton<IFunctionRuntimeRouteResolver, FunctionRuntimeRouteResolver>();
            services.TryAddScoped<IStorageProfileResolver, StorageProfileResolver>();
            services.TryAddScoped<IWebServiceSourceBuildDeploymentService, WebServiceSourceBuildDeploymentService>();
            services.TryAddScoped<IStorageDiscoveryService, KubernetesStorageDiscoveryService>();
            services.TryAddScoped<ITenantContextAccessor, TenantContextAccessor>();
            return services;
        }

        public IServiceCollection AddGitProvidersFeature()
        {
            services.AddGitProviderCapabilities();
            services.AddMemoryCache();
            services.AddDataProtection().SetApplicationName("Pole");
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<IGitHubSetupTokenService, GitHubSetupTokenService>();
            services.TryAddSingleton<IGitHubAppJwtFactory, GitHubAppJwtFactory>();
            services.TryAddSingleton<IRepositorySourceAnalyzer, RepositorySourceAnalyzer>();
            services.TryAddScoped<IGitHubSetupService, GitHubSetupService>();
            services.TryAddScoped<IGitProviderRepositoryService, GitProviderRepositoryService>();
            services.AddHttpClient<IGitHubAppClient, GitHubAppClient>(client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Pole/1.0");
            });
            return services;
        }

        public IServiceCollection AddContainerRegistriesFeature()
        {
            services.AddContainerRegistryCapabilities();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<IRegistryCredentialProtector, RegistryCredentialProtector>();
            services.TryAddScoped<IRegistryCredentialResolver, RegistryCredentialResolver>();
            services.TryAddScoped<IRegistryCredentialRewrapService, RegistryCredentialRewrapService>();
            services.TryAddScoped<IRuntimeRegistryAccessProvider, RuntimeRegistryAccessProvider>();
            services.TryAddScoped<IRegistryProvisioningCleanupService, RegistryProvisioningCleanupService>();
            services.TryAddScoped<IContainerRegistryProvisioningService, ContainerRegistryProvisioningService>();
            services.AddHttpClient<IContainerRegistryClient, HarborContainerRegistryClient>(client =>
                    client.Timeout = TimeSpan.FromMinutes(2))
                .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
                {
                    // Harbor's Basic-auth API can set a browser session cookie. Replaying it without
                    // the corresponding CSRF context makes subsequent mutating requests fail with 403.
                    UseCookies = false
                });
            return services;
        }

        public IServiceCollection AddSourceBuildsFeature()
        {
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddScoped<ISourceBuildReferenceLookup, SourceBuildReferenceLookup>();
            services.TryAddSingleton<ISourceBuildJobDispatcher, HangfireSourceBuildJobDispatcher>();
            services.TryAddSingleton<IRepositoryArchiveExtractor, RepositoryArchiveExtractor>();
            services.TryAddSingleton<IProcessRunner, ProcessRunner>();
            services.TryAddSingleton<IBuildKitClient, BuildKitClient>();
            services.TryAddScoped<ISourceBuildWorkspaceService, SourceBuildWorkspaceService>();
            services.TryAddScoped<ISourceBuildJobExecutor, SourceBuildJobExecutor>();
            return services;
        }

        public IServiceCollection AddUsersFeature(IConfiguration configuration)
        {
            services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakOptions.SectionName));
            services.AddHttpClient<IIdentityProvider, KeycloakIdentityProvider>();
            return services;
        }

        public IServiceCollection AddSettings(IConfiguration configuration)
        {
            services.AddSingleton<
                IValidateOptions<OutboxSettings>,
                OutboxSettingsValidator>();
            services.AddOptions<OutboxSettings>()
                .Bind(configuration.GetSection(OutboxSettings.SectionName))
                .ValidateOnStart();
            services.AddSingleton(
                sp => sp.GetRequiredService<IOptions<OutboxSettings>>().Value);
            services.AddSingleton<
                IValidateOptions<KubernetesOptions>,
                RuntimeCellNamespaceOptionsValidator>();
            services.AddOptions<KubernetesOptions>()
                .Bind(configuration.GetSection(KubernetesOptions.SectionName))
                .ValidateOnStart();
            services.AddSingleton(
                sp => sp.GetRequiredService<IOptions<KubernetesOptions>>().Value);
            services.AddSettingsSection<PostgresSettings>(PostgresSettings.SectionName, configuration);
            services.AddOptions<QuotaSettings>()
                .Bind(configuration.GetSection(QuotaSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.AddSingleton(sp => sp.GetRequiredService<IOptions<QuotaSettings>>().Value);
            services.AddSettingsSection<KeycloakOptions>(KeycloakOptions.SectionName, configuration);
            services.AddSettingsSection<GitHubAppOptions>(GitHubAppOptions.SectionName, configuration);
            services.AddSettingsSection<HarborOptions>(HarborOptions.SectionName, configuration);
            services.AddSettingsSection<SourceBuildExecutionOptions>(SourceBuildExecutionOptions.SectionName, configuration);
            services.AddSettingsSection<BuildKitOptions>(BuildKitOptions.SectionName, configuration);
            return services;
        }

        public IServiceCollection AddOutboxEventAssemblies(params Assembly[] assemblies)
        {
            services.AddSingleton(new OutboxEventAssemblyCatalog(assemblies));
            return services;
        }
        
        private void AddSettingsSection<TSettings>(string sectionName, IConfiguration configuration)
            where TSettings : class
        {
            services.Configure<TSettings>(configuration.GetSection(sectionName));
            services.AddSingleton<TSettings>(sp => sp.GetRequiredService<IOptions<TSettings>>().Value);
        }

        public IServiceCollection AddHangfireWithMongo(IConfiguration configuration)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddHangfire((serviceProvider, config) =>
            {
                var mongoClient = serviceProvider.GetRequiredService<IMongoClient>();
                
                config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UseMongoStorage(mongoClient, "pole-database", new MongoStorageOptions
                    {
                        MigrationOptions = new MongoMigrationOptions
                        {
                            MigrationStrategy = new MigrateMongoMigrationStrategy(),
                            BackupStrategy = new CollectionMongoBackupStrategy()
                        },
                        Prefix = "hangfire",
                        CheckConnection = false,
                        CheckQueuedJobsStrategy = CheckQueuedJobsStrategy.TailNotificationsCollection,
                        ConnectionCheckTimeout = TimeSpan.FromSeconds(30)
                    });
            });

            services.AddHangfireServer(options =>
            {
                options.ServerName = $"{Environment.MachineName}:pole-server";
                options.ServerTimeout = TimeSpan.FromMinutes(5);
                options.ServerCheckInterval = TimeSpan.FromMinutes(1);
                options.HeartbeatInterval = TimeSpan.FromSeconds(30);
            });
            services.AddScoped<OutboxProcessorJob>();
            services.AddScoped<IOutboxPublisher, MassTransitOutboxPublisher>();
            services.AddSingleton<OutboxMetrics>();
            services.AddSingleton<OutboxRetryDelayPolicy>();

            return services;
        }
        
        
    }

    extension(WebApplicationBuilder builder)
    {
        public  WebApplicationBuilder AddSecurity()
        {
            var configuredValidIssuer = builder.Configuration["Authentication:ValidIssuer"];
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;

                })
                .AddKeycloakJwtBearer(
                    serviceName: "pole-keycloak",
                    realm: "pole.control-plane",
                    options =>
                    {
                        options.MapInboundClaims = false;
                        options.Audience = "pole.control-plane.api";
                        if (builder.Environment.IsDevelopment())
                            options.RequireHttpsMetadata = false;
                    });
            if (!string.IsNullOrWhiteSpace(configuredValidIssuer))
            {
                builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = configuredValidIssuer;
                    options.TokenValidationParameters.ValidIssuers = [configuredValidIssuer];
                });
            }
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(Policy.Admin, policy =>
                    policy.RequireClaim(Claims.Roles, Roles.Admin));
            });

            return builder;
        }
    }

    extension(WebApplication app)
    {
        public WebApplication UseHangfire()
        {
            var outboxSettings = app.Services.GetRequiredService<IOptions<OutboxSettings>>().Value;
            if (app.Environment.IsDevelopment())
                app.UseHangfireDashboard();

            RecurringJob.AddOrUpdate<OutboxProcessorJob>(
                "process-outbox-messages",
                job => job.ProcessOutboxMessagesAsync(CancellationToken.None),
                outboxSettings.ProcessingCronExpression);

            RecurringJob.AddOrUpdate<OutboxProcessorJob>(
                "cleanup-outbox-messages",
                job => job.CleanupMessagesAsync(CancellationToken.None),
                Cron.Daily(3));

            RecurringJob.AddOrUpdate<SourceBuildQueueRecoveryJob>(
                "recover-source-build-queue",
                job => job.RecoverAsync(),
                Cron.Minutely);

            RecurringJob.AddOrUpdate<RuntimeCellRecoveryJob>(
                "recover-runtime-cells",
                job => job.RecoverAsync(CancellationToken.None),
                Cron.Minutely);

            RecurringJob.AddOrUpdate<ResourceDeploymentRecoveryJob>(
                "recover-resource-deployments",
                job => job.RecoverAsync(CancellationToken.None),
                Cron.Minutely);

            RecurringJob.AddOrUpdate<IRegistryProvisioningCleanupService>(
                "cleanup-container-registry-provisioning",
                job => job.ReconcileAsync(CancellationToken.None),
                Cron.Minutely);

            return app;
        }
    }
}
