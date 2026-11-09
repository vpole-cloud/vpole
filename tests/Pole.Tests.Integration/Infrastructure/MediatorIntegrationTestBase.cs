using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.Testing;
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
        Timings = new TestTimings(GetType());
    }

    protected MongoReplicaSetFixture Fixture { get; }
    protected TestTimings Timings { get; }
    protected MongoTestStore DbContext => Fixture.DbContext;
    protected IMediator Mediator => _scope!.ServiceProvider.GetRequiredService<IMediator>();
    protected IServiceProvider Services => _scope!.ServiceProvider;

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public virtual async Task InitializeAsync()
    {
        using (Timings.Measure("database.reset"))
            await Fixture.ResetDatabaseAsync();
        using (Timings.Measure("seed.base"))
        {
            await Fixture.DbContext.StorageProfiles.InsertOneAsync(TestData.SeedStorageProfile());
            await Fixture.Database
                .GetCollection<ComputeProfile>("ComputeProfiles")
                .InsertOneAsync(TestData.SeedComputeProfile());
            await Fixture.DbContext.Tenants.InsertOneAsync(TenantFactory.Create(
                "Test tenant",
                TestData.TenantId));
        }
        _serviceProvider = TestServiceProviderFactory.Create(Fixture, ConfigureServices, Timings);
        using (Timings.Measure("mediator.scope.create"))
            _scope = _serviceProvider.CreateScope();
    }

    public virtual Task DisposeAsync()
    {
        using (Timings.Measure("mediator.scope.dispose"))
            _scope?.Dispose();
        using (Timings.Measure("mediator.provider.dispose"))
            _serviceProvider?.Dispose();
        return Task.CompletedTask;
    }
}
