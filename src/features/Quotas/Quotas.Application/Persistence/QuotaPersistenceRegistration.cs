using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.Quotas.Application.Persistence.Policies;
using Pole.Features.Quotas.Application.Persistence.Reservations;
using Pole.Features.Quotas.Application.Persistence.Usage;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Persistence;

internal static class QuotaPersistenceRegistration
{
    public static IServiceCollection AddQuotaPersistence(this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<TenantQuotaPolicy>("TenantQuotaPolicies"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<TenantQuotaUsage>("TenantQuotaUsages"));
        services.TryAddSingleton(serviceProvider => serviceProvider
            .GetRequiredService<IMongoDatabase>()
            .GetCollection<QuotaReservation>("QuotaReservations"));

        AddCapability<MongoReserveQuotaReservation, ReserveQuotaReservation>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoCommitQuotaReservation, CommitQuotaReservation>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoCancelQuotaReservation, CancelQuotaReservation>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoApplyQuotaUsageDelta, ApplyQuotaUsageDelta>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadTenantQuotaPolicy, LoadTenantQuotaPolicy>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadTenantQuotaUsage, LoadTenantQuotaUsage>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoLoadActiveQuotaReservations, LoadActiveQuotaReservations>(
            services,
            implementation => implementation.InvokeAsync);
        AddCapability<MongoPersistTenantQuotaPolicy, PersistTenantQuotaPolicy>(
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
        services.AddScoped<TImplementation>();
        services.AddScoped(serviceProvider =>
            bind(serviceProvider.GetRequiredService<TImplementation>()));
    }
}
