using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal static class SecretPersistenceRegistration
{
    internal static IServiceCollection AddSecretPersistence(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<Secret>("Secrets"));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<SecretVersion>("SecretVersions"));
        services.AddScoped<MongoLoadSecretById>();
        services.AddScoped<LoadSecretById>(sp => sp.GetRequiredService<MongoLoadSecretById>().InvokeAsync);
        services.AddScoped<MongoLoadSecretPage>();
        services.AddScoped<LoadSecretPage>(sp => sp.GetRequiredService<MongoLoadSecretPage>().InvokeAsync);
        services.AddScoped<MongoPersistCreatedSecret>();
        services.AddScoped<PersistCreatedSecret>(sp => sp.GetRequiredService<MongoPersistCreatedSecret>().InvokeAsync);
        services.AddScoped<MongoPersistRotatedSecret>();
        services.AddScoped<PersistRotatedSecret>(sp => sp.GetRequiredService<MongoPersistRotatedSecret>().InvokeAsync);
        services.AddScoped<MongoPersistSecretBinding>();
        services.AddScoped<PersistSecretBinding>(sp => sp.GetRequiredService<MongoPersistSecretBinding>().InvokeAsync);
        services.AddScoped<MongoRemoveSecretBinding>();
        services.AddScoped<RemoveSecretBinding>(sp => sp.GetRequiredService<MongoRemoveSecretBinding>().InvokeAsync);
        services.AddScoped<MongoDeleteStoredSecret>();
        services.AddScoped<DeleteStoredSecret>(sp => sp.GetRequiredService<MongoDeleteStoredSecret>().InvokeAsync);
        services.AddScoped<MongoRemoveConsumerSecretBindings>();
        services.AddScoped<RemoveConsumerSecretBindings>(sp => sp.GetRequiredService<MongoRemoveConsumerSecretBindings>().InvokeAsync);
        services.AddScoped<OutboxStoreSecretChange>();
        services.AddScoped<StoreSecretChange>(sp => sp.GetRequiredService<OutboxStoreSecretChange>().InvokeAsync);
        services.AddScoped<EnvelopeEncryptSecretVersion>();
        services.AddScoped<EncryptSecretVersion>(sp => sp.GetRequiredService<EnvelopeEncryptSecretVersion>().InvokeAsync);
        services.AddScoped<IIsolationScopeReferenceGuard, SecretScopeReferenceGuard>();
        return services;
    }
}
