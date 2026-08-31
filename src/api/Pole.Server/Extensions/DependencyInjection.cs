using System.Reflection;
using FluentValidation;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application;
using Pole.Mediator;
using Pole.Observability;

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
            services.AddScoped<IMediator, Pole.Mediator.Mediator>();
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantAuthorizationBehavior<,>));
            services.AddTenantMemberAuthorizationBehavior();

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
                definition == typeof(IMediatorOutcomeHandler<,>);
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
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<MongoRunTransaction>();
            services.AddSingleton<MigrationRunner>();
            return services;
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
            if (app.Environment.IsDevelopment())
                app.UseHangfireDashboard();
            return app;
        }
    }
}
