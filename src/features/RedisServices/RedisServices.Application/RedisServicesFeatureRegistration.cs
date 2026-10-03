using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Database;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Application.Migrations;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Application.Persistence.Lifecycle;
using Pole.Features.RedisServices.Application.Search;
using Pole.Features.Search.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.RedisServices.Contracts;

namespace Pole.Features.RedisServices.Application;

public static class RedisServicesFeatureRegistration
{
    public static IServiceCollection AddRedisServicesFeature(
        this IServiceCollection services)
    {
        services.AddSingleton<IMigration>(new FeatureScopedMigration(
            "pole.redis", new RedisNameIndexesMigration()));
        services.AddSingleton<SearchSourceType>(RedisServiceSearchProjection.SourceType);
        services.AddRedisPersistence();
        services.AddSingleton(new ResourceConnectionSourceCapability(
            RedisResourceDeployment.ResourceType));
        services.TryAddSingleton<RedisServiceCountQuota>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Quota, RedisServiceCountQuota>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IQuotaProfile, RedisQuotaProfile>());
        services.TryAddScoped<RunMongoOutcomeTransaction<
            FinalizeRedisDeletionOutcome>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<FinalizeRedisDeletionOutcome>);
        services.TryAddScoped<RedisResourceDeploymentAdapter>();
        services.TryAddSingleton(RedisResourceDeploymentAdapterRegistration.Descriptor);
        return services;
    }
}
