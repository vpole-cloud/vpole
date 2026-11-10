using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using Npgsql;
using Pole.Testing;
using StackExchange.Redis;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests.Integration;

public sealed class DatabaseTransportTests
{
    [Fact]
    public async Task PostgreSqlTransactionAndLargeBinaryPayloadCrossProductionBroker()
    {
        ContainerRuntimeBootstrap.ConfigureFromLabStateIfAvailable();
        var identity = TestRunIdentity.Create();
        var password = Guid.NewGuid().ToString("N");
        await using var postgres = new ContainerBuilder("postgres:18-alpine").WithTestOwnership(identity)
            .WithEnvironment("POSTGRES_PASSWORD", password)
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await postgres.StartAsync();
        await WaitForHostPortAsync(postgres.GetMappedPublicPort(5432));
        await using var broker = await DatabaseBrokerFixture.StartAsync(postgres.GetMappedPublicPort(5432));
        await using var forward = await broker.ForwardAsync();
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = forward.Port,
                Username = "postgres",
                Password = password,
                Database = "postgres",
                Pooling = false,
                Timeout = 5,
                CommandTimeout = 10,
                SslMode = SslMode.Disable,
            }.ConnectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand(
                         "CREATE TEMP TABLE broker_transport (id integer, payload bytea)",
                         connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO broker_transport VALUES (1, $1)",
                connection,
                transaction);
            insert.Parameters.AddWithValue(payload);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO broker_transport VALUES (2, NULL)",
                connection,
                transaction);
            await insert.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        await using var read = new NpgsqlCommand("SELECT payload FROM broker_transport WHERE id = 1", connection);
        Assert.Equal(payload, Assert.IsType<byte[]>(await read.ExecuteScalarAsync()));
        await using var count = new NpgsqlCommand("SELECT count(*) FROM broker_transport", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        Assert.True(broker.StartedSessions > 0);
        Assert.True(broker.RenewedSessions > 0);
    }

    [Fact]
    public async Task RedisRequestResponseAndLargeBinaryPayloadCrossProductionBroker()
    {
        ContainerRuntimeBootstrap.ConfigureFromLabStateIfAvailable();
        var identity = TestRunIdentity.Create();
        await using var redis = new ContainerBuilder("redis:8.2-alpine").WithTestOwnership(identity)
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
        await redis.StartAsync();
        await WaitForHostPortAsync(redis.GetMappedPublicPort(6379));
        await using var broker = await DatabaseBrokerFixture.StartAsync(redis.GetMappedPublicPort(6379));
        await using var forward = await broker.ForwardAsync();
        ConnectionMultiplexer connection;
        try
        {
            connection = await ConnectionMultiplexer.ConnectAsync(
                new ConfigurationOptions
                {
                    EndPoints = { { "127.0.0.1", forward.Port } },
                    AbortOnConnectFail = true,
                    ConnectRetry = 0,
                    ConnectTimeout = 8000,
                    AsyncTimeout = 8000,
                });
        }
        catch (RedisConnectionException error)
        {
            throw new InvalidOperationException(
                $"Redis transport setup failed; started={broker.StartedSessions}, " +
                $"renewed={broker.RenewedSessions}, closed={broker.CloseReasons}, " + $"forward={forward.Diagnostics}",
                error);
        }

        using (connection)
        {
            var key = Guid.NewGuid().ToString("N");
            var payload = new byte[128 * 1024];
            Random.Shared.NextBytes(payload);
            Assert.True(await connection.GetDatabase().StringSetAsync(key, payload));
            Assert.Equal(payload, (byte[]?)await connection.GetDatabase().StringGetAsync(key));
            Assert.True(broker.StartedSessions > 0);
            Assert.True(broker.RenewedSessions > 0);
        }
    }

    private static async Task WaitForHostPortAsync(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var probe = new TcpClient();
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return;
            }
            catch (SocketException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(50, timeout.Token);
            }
        }
    }
}
