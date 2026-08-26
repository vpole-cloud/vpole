using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Application.Persistence.Lifecycle;
using Pole.Features.RedisServices.Application.Persistence.Mutations;
using Pole.Features.RedisServices.Application.Persistence.Reads;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence;

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
