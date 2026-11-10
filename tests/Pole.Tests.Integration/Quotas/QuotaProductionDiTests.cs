using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Policies;
using Pole.Features.Quotas.Application.Reservations;
using Pole.Features.Quotas.Application.Usage;
using Pole.Features.Quotas.Contracts;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

public sealed class QuotaProductionDiTests
{
    private readonly IMongoDatabase database
        = new MongoClient("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100").GetDatabase("di-only");

    [Fact]
    public void FeatureRegistration_ResolvesTheQuotaCapabilityGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoClient>(database.Client);
        services.AddSingleton(database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton(TimeProvider.System);
        var configuration = new ConfigurationBuilder().Build();
        services.AddQuotasFeature(configuration);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.Name.Contains("QuotaDbContext", StringComparison.Ordinal));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(ReserveQuotaReservation) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(CommitQuotaReservation) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(CancelQuotaReservation) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(ApplyQuotaUsageDelta) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IQuotaAdmissionExecutor) &&
                descriptor.Lifetime == ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        AssertResolved<ReserveQuotaReservation>(scope.ServiceProvider);
        AssertResolved<CommitQuotaReservation>(scope.ServiceProvider);
        AssertResolved<CancelQuotaReservation>(scope.ServiceProvider);
        AssertResolved<ApplyQuotaUsageDelta>(scope.ServiceProvider);
        AssertResolved<LoadTenantQuotaPolicy>(scope.ServiceProvider);
        AssertResolved<LoadTenantQuotaUsage>(scope.ServiceProvider);
        AssertResolved<LoadActiveQuotaReservations>(scope.ServiceProvider);
        AssertResolved<PersistTenantQuotaPolicy>(scope.ServiceProvider);
        AssertResolved<IQuotaAdmissionExecutor>(scope.ServiceProvider);
    }

    private static void AssertResolved<T>(IServiceProvider provider)
        where T : notnull => Assert.NotNull(provider.GetRequiredService<T>());
}
