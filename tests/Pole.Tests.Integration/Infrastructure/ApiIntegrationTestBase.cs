using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[Collection(IntegrationTestCollection.Name)]
public abstract class ApiIntegrationTestBase : IAsyncLifetime
{
    private TestApiApplication? _application;

    protected ApiIntegrationTestBase(MongoReplicaSetFixture fixture)
    {
        Fixture = fixture;
    }

    protected MongoReplicaSetFixture Fixture { get; }
    protected HttpClient Client => _application!.Client;

    public virtual async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        _application = await TestApiApplication.StartAsync(Fixture);
    }

    public virtual async Task DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync();
        }
    }
}
