using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public abstract class MediatorIntegrationTestBase : IAsyncLifetime
{
    private ServiceProvider? _serviceProvider;
    private IServiceScope? _scope;

    private readonly bool _isolatedDatabase;

    protected MediatorIntegrationTestBase(MongoReplicaSetFixture fixture) : this(fixture, false) { }

    protected MediatorIntegrationTestBase(
        MongoReplicaSetFixture fixture,
        bool isolatedDatabase,
        IntegrationSchemaProfile schemaProfile = IntegrationSchemaProfile.Full)
    {
        if (!isolatedDatabase && schemaProfile != IntegrationSchemaProfile.Full)
            throw new ArgumentException("Shared tenant databases require the Full schema profile.", nameof(schemaProfile));
        _isolatedDatabase = isolatedDatabase;
        Fixture = isolatedDatabase ? fixture.CreateDatabase(schemaProfile) : fixture.SharedDatabase;
        TestScope = IntegrationTestScope.Create(Fixture);
        Data = new IntegrationTestData(TestScope);
        Timings = new TestTimings(GetType());
    }

    protected TestDatabaseScope Fixture { get; }
    protected IntegrationTestScope TestScope { get; }
    protected IntegrationTestData Data { get; }
    protected TestTimings Timings { get; }
    protected MongoTestStore DbContext => Fixture.DbContext;
    protected IMediator Mediator => _scope!.ServiceProvider.GetRequiredService<IMediator>();
    protected IServiceProvider Services => _scope!.ServiceProvider;

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public virtual async Task InitializeAsync()
    {
        using (Timings.Measure("database.prepare"))
            await Fixture.PrepareAsync();
        using (Timings.Measure("seed.base"))
            await TestScope.SeedAsync();
        _serviceProvider = TestServiceProviderFactory.Create(Fixture, ConfigureServices, Timings, TestScope);
        using (Timings.Measure("mediator.scope.create"))
            _scope = _serviceProvider.CreateScope();
    }

    public virtual async Task DisposeAsync()
    {
        try
        {
            using (Timings.Measure("mediator.scope.dispose"))
                _scope?.Dispose();
        }
        finally
        {
            try
            {
                using (Timings.Measure("mediator.provider.dispose"))
                    if (_serviceProvider is not null) await _serviceProvider.DisposeAsync();
            }
            finally
            {
                if (_isolatedDatabase)
                    using (Timings.Measure("database.dispose")) await Fixture.DisposeAsync();
            }
        }
    }
}

public abstract class IsolatedMediatorIntegrationTestBase(
    MongoReplicaSetFixture fixture,
    IntegrationSchemaProfile schemaProfile = IntegrationSchemaProfile.Full)
    : MediatorIntegrationTestBase(fixture, true, schemaProfile), IIsolatedIntegrationTest;
