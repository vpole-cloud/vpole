using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Mediator;
using Pole.Server.Database;
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
    protected MongoDbContext DbContext => Fixture.DbContext;
    protected IMediator Mediator => _scope!.ServiceProvider.GetRequiredService<IMediator>();
    protected IServiceProvider Services => _scope!.ServiceProvider;

    public virtual async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        await Fixture.DbContext.StorageProfiles.InsertOneAsync(TestData.SeedStorageProfile());
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
