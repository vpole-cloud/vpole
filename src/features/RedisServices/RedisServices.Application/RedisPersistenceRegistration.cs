using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.RedisServices.Application.Declarations;
using Pole.Features.RedisServices.Application.Lifecycle;
using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.RedisServices.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Contracts;

namespace Pole.Features.RedisServices.Application;

public static class RedisPersistenceRegistration
{
    internal const string RedisCollectionName = "RedisServices";

    internal static IServiceCollection AddRedisPersistence(
        this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider =>
            serviceProvider
                .GetRequiredService<IMongoDatabase>()
                .GetCollection<Redis>(RedisCollectionName));
        services.AddScoped<IResourceConnectionDestinationCapability,
            MongoLoadRedisConnectionEndpoints>();
        services.AddScoped<IResourceConnectionDestinationNameCapability,
            MongoLoadRedisConnectionEndpoints>();
        services.AddScoped<RedisDeclarationPlanner>();
        services.AddScoped<RedisDeclarationReferences>();
        services.AddScoped<RedisDeclarationExecution>();
        services.AddScoped<RedisDeclarationAdoption>();
        services.AddScoped<RedisDeclarationLifecycle>();
        services.AddScoped<DeclarativeRedisProvider>();
        services.AddScoped<IDeclarativeResourceProvider>(sp => sp.GetRequiredService<DeclarativeRedisProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(sp =>
            sp.GetRequiredService<DeclarativeRedisProvider>());
        services.AddScoped<IDeclarativeLifecycleDependencyProvider>(sp =>
            sp.GetRequiredService<DeclarativeRedisProvider>());
        services.AddScoped<MongoLoadRedisById>();
        services.AddScoped<MongoLoadRedisPage>();
        services.AddScoped<MongoPersistCreatedRedis>();
        services.AddScoped<MongoPersistUpdatedRedis>();
        services.AddScoped<MongoBeginRedisDeletion>();
        services.AddScoped<MongoFinalizeRedisDeletion>();

        services.AddScoped<LoadRedisById>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoLoadRedisById>().InvokeAsync);
        services.AddScoped<LoadRedisPage>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoLoadRedisPage>().InvokeAsync);
        services.AddScoped<PersistCreatedRedis>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoPersistCreatedRedis>().InvokeAsync);
        services.AddScoped<PersistUpdatedRedis>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoPersistUpdatedRedis>().InvokeAsync);
        services.AddScoped<BeginRedisDeletion>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoBeginRedisDeletion>().InvokeAsync);
        services.AddScoped<FinalizeRedisDeletion>(serviceProvider =>
            serviceProvider.GetRequiredService<MongoFinalizeRedisDeletion>().InvokeAsync);
        return services;
    }
}
