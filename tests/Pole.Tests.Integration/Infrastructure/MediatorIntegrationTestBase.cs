using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Name)]
public abstract class MediatorIntegrationTestBase : IAsyncLifetime
{
    private ServiceProvider? _serviceProvider;
    private IServiceScope? _scope;

    protected MediatorIntegrationTestBase(MongoReplicaSetFixture fixture)
    {
        Fixture = fixture;
    }

    protected MongoReplicaSetFixture Fixture { get; }
    protected MongoTestStore DbContext => Fixture.DbContext;
    protected IMediator Mediator => _scope!.ServiceProvider.GetRequiredService<IMediator>();
    protected IServiceProvider Services => _scope!.ServiceProvider;

    public virtual async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        await Fixture.DbContext.StorageProfiles.InsertOneAsync(TestData.SeedStorageProfile());
        await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(TestData.SeedComputeProfile());
        await Fixture.DbContext.Tenants.InsertOneAsync(TenantFactory.Create(
            "Test tenant",
            TestData.TenantId));
        _serviceProvider = TestServiceProviderFactory.Create(Fixture);
        _scope = _serviceProvider.CreateScope();
    }

    public virtual Task DisposeAsync()
    {
        _scope?.Dispose();
        _serviceProvider?.Dispose();
        return Task.CompletedTask;
    }
}
