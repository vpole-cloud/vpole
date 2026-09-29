using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Pole.Testing;
using Xunit;

namespace Pole.PrivateAccess.Prototype.Tests.Integration;

public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly TestRunIdentity identity = TestRunIdentity.Create();
    public IContainer Redis { get; }
    public IContainer Postgres { get; }
    public string Password { get; } = Guid.NewGuid().ToString("N");

    public DatabaseFixture()
    {
        ContainerRuntimeBootstrap.ConfigureFromLabStateIfAvailable();
        Redis = new ContainerBuilder("redis:8.2-alpine")
            .WithTestOwnership(identity)
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
        Postgres = new ContainerBuilder("postgres:18-alpine")
            .WithTestOwnership(identity)
            .WithEnvironment("POSTGRES_PASSWORD", Password)
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Redis.StartAsync();
            await Postgres.StartAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try { await Redis.DisposeAsync(); }
        finally { await Postgres.DisposeAsync(); }
    }
}
