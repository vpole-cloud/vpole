using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Application.Persistence;
using Pole.Features.Quotas.Application.Persistence.Policies;
using Pole.Features.Quotas.Application.Persistence.Reservations;
using Pole.Features.Quotas.Application.Persistence.Usage;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

[Collection(IntegrationTestCollection.Name)]
public sealed class QuotaProductionDiTests(MongoReplicaSetFixture fixture)
{
    [Fact]
    public void FeatureRegistration_ResolvesTheQuotaCapabilityGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoClient>(fixture.Client);
        services.AddSingleton<IMongoDatabase>(fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new QuotaSettings());
        services.AddQuotasFeature(typeof(RedisQuotaProfile).Assembly);

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.Name.Contains("QuotaDbContext", StringComparison.Ordinal));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ReserveQuotaReservation) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(CommitQuotaReservation) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(CancelQuotaReservation) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ApplyQuotaUsageDelta) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IQuotaAdmissionExecutor) &&
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
        where T : notnull =>
        Assert.NotNull(provider.GetRequiredService<T>());
}
