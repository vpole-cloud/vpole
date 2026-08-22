using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Database;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application;

public static class RedisServicesFeatureRegistration
{
    public static IServiceCollection AddRedisServicesFeature(
        this IServiceCollection services)
    {
        services.AddRedisPersistence();
        services.AddRedisHttpMappings();
        services.TryAddScoped<RunMongoOutcomeTransaction<
            Result<Unit, RedisDeletionFinalizationConflict>>>(serviceProvider =>
            serviceProvider
                .GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<Result<Unit, RedisDeletionFinalizationConflict>>);
        services.TryAddScoped<RedisResourceDeploymentAdapter>();
        services.TryAddSingleton(RedisResourceDeploymentAdapterRegistration.Descriptor);
        return services;
    }
}
