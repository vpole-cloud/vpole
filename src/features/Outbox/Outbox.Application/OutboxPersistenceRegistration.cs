using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.Outbox.Application.Delivery;
using Pole.Features.Outbox.Application.Writes;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Outbox.Application;

public static class OutboxPersistenceRegistration
{
    public static IServiceCollection AddOutboxPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<OutboxMessage>("OutboxMessages"));

        AddCapability<MongoLoadDueOutboxMessages, LoadDueOutboxMessages>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoTryPersistOutboxMessageTransition, TryPersistOutboxMessageTransition>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoCleanupTerminalOutboxMessages, CleanupTerminalOutboxMessages>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadOutboxQueueSnapshot, LoadOutboxQueueSnapshot>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoStoreOutboxWrites, StoreOutboxWrites>(
            services,
            implementation => implementation.InvokeAsync);
        return services;
    }

    private static void AddCapability<TImplementation, TDelegate>(
        IServiceCollection services,
        Func<TImplementation, TDelegate> bind)
        where TImplementation : class
        where TDelegate : Delegate
    {
        services.TryAddSingleton<TImplementation>();
        services.TryAddSingleton(serviceProvider => bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
