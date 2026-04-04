using System.Reflection;
using FluentValidation;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using k8s;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox;
using Pole.Mediator;

namespace Pole.Server.Extensions;

public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPoleMediator(params Assembly[] assemblies)
        {
            services.AddScoped<IMediator, Pole.Mediator.Mediator>();
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

            foreach (var assembly in assemblies)
            {
                var handlerTypes = assembly.GetTypes()
                    .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces()
                        .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMediatorHandler<,>)));

                foreach (var handlerType in handlerTypes)
                {
                    var interfaceType = handlerType.GetInterfaces()
                        .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMediatorHandler<,>));
                    services.AddScoped(interfaceType, handlerType);
                }
            }

            return services;
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
            services.AddSingleton<IKubernetes>(_ =>
            {
                var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
                return new Kubernetes(config);
            });
            return services;
        }
        public IServiceCollection AddEventBus(IConfiguration configuration, Assembly[] assemblies)
        {
            services.AddMassTransit(x =>
            {
                x.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(configuration.GetConnectionString("pole-rabbitmq"));
                    cfg.ConfigureEndpoints(context);
                });
                x.AddConsumers(assemblies);
            });
            return services;
        }
        
        public IServiceCollection AddDatabase()
        {
            services.AddSingleton<IMongoDatabase>(sp =>
                sp.GetRequiredService<IMongoClient>().GetDatabase("pole-database"));
            services.AddSingleton<MongoDbContext>();
            services.AddSingleton<MigrationRunner>();
            return services;
        }

        public IServiceCollection AddSettings(IConfiguration configuration)
        {
            services.AddSettingsSection<OutboxSettings>(OutboxSettings.SectionName, configuration);
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

            return services;
        }
        
        
    }

    extension(WebApplicationBuilder builder)
    {
        public  WebApplicationBuilder AddSecurity()
        {
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;

                })
                .AddKeycloakJwtBearer(
                    serviceName: "keycloak",
                    realm: "pole.control-plane",
                    options =>
                    {
                        options.Audience = "pole.control-plane.api";
                        if (builder.Environment.IsDevelopment())
                        {
                            options.RequireHttpsMetadata = false;
                            options.MetadataAddress =
                                "http://localhost:8080/realms/pole.control-plane/.well-known/openid-configuration";
                        }
                    });
            builder.Services.AddAuthorization();

            return builder;
        }
    }

    extension(WebApplication app)
    {
        public WebApplication UseHangfire()
        {
            try
            {
                var outboxSettings = app.Services.GetRequiredService<IOptions<OutboxSettings>>().Value;

                // Configure Hangfire Dashboard (open to everyone for now)
                app.UseHangfireDashboard();

                // Schedule recurring jobs for outbox processing
                RecurringJob.AddOrUpdate<OutboxProcessorJob>(
                    "process-outbox-messages",
                    job => job.ProcessOutboxMessagesAsync(),
                    outboxSettings.ProcessingCronExpression);

                RecurringJob.AddOrUpdate<OutboxProcessorJob>(
                    "cleanup-outbox-messages",
                    job => job.CleanupProcessedMessagesAsync(),
                    Cron.Daily(3)); // Daily at 3 AM
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Failed to initialize Hangfire. Background jobs will not be available.");
            }

            return app;
        }
    }
}
