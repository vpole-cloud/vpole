using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Outbox.Application.Persistence;
using Pole.Features.Outbox.Contracts;
using System.Net;
using System.Net.Sockets;
using Pole.Server.Database;
using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class MongoReplicaSetFixture : IAsyncLifetime
{
    private const string DefaultMongoImage = "mongo:7.0";

    private readonly int _mongoPort;
    private readonly IContainer _container;
    private ServiceProvider? _outboxServices;

    public string ConnectionString { get; private set; } = string.Empty;
    public string DirectConnectionString { get; private set; } = string.Empty;
    public IMongoClient Client { get; private set; } = null!;
    public IMongoDatabase Database { get; private set; } = null!;
    public MongoDbContext DbContext { get; private set; } = null!;
    public StoreOutboxWrites StoreOutboxWrites =>
        _outboxServices!.GetRequiredService<StoreOutboxWrites>();

    public MongoReplicaSetFixture()
    {
        _mongoPort = GetFreePort();
        var mongoImage = Environment.GetEnvironmentVariable("POLE_TEST_MONGO_IMAGE");
        if (string.IsNullOrWhiteSpace(mongoImage))
            mongoImage = DefaultMongoImage;

        _container = new ContainerBuilder(mongoImage)
            .WithPortBinding(_mongoPort, _mongoPort)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(_mongoPort))
            .WithCommand("mongod", "--replSet", "rs0", "--bind_ip_all", "--port", _mongoPort.ToString())
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        DirectConnectionString = $"mongodb://127.0.0.1:{_mongoPort}/?directConnection=true";
        ConnectionString = $"mongodb://127.0.0.1:{_mongoPort}/?replicaSet=rs0";

        await InitializeReplicaSetAsync();

        Client = new MongoClient(ConnectionString);
        Database = Client.GetDatabase("pole-integration-tests");
        DbContext = new MongoDbContext(Client, Database);
        _outboxServices = new ServiceCollection()
            .AddSingleton(Database)
            .AddOutboxPersistence()
            .BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _outboxServices!.DisposeAsync();
        await _container.DisposeAsync();
    }

    public async Task ResetDatabaseAsync()
    {
        await Database.Client.DropDatabaseAsync(Database.DatabaseNamespace.DatabaseName);
        Database = Client.GetDatabase("pole-integration-tests");
        DbContext = new MongoDbContext(Client, Database);
    }

    private async Task InitializeReplicaSetAsync()
    {
        var initCommand =
            $"mongosh --quiet --port {_mongoPort} --eval \"rs.initiate({{ _id: 'rs0', members: [{{ _id: 0, host: '127.0.0.1:{_mongoPort}' }}] }})\"";
        var result = await _container.ExecAsync(["/bin/sh", "-c", initCommand]);

        if (result.ExitCode != 0 && !result.Stderr.Contains("already initialized", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Failed to initialize Mongo replica set: {result.Stderr}");
        }

        var timeoutAt = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                var client = new MongoClient(ConnectionString.Replace("replicaSet=rs0", "directConnection=true"));
                var admin = client.GetDatabase("admin");
                var status = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetStatus", 1));
                if (status.TryGetValue("ok", out var ok) && ok.ToDouble() == 1)
                {
                    return;
                }
            }
            catch
            {
                // Replica set election takes a moment; keep polling.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Mongo replica set did not become ready in time.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
