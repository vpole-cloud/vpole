using System.Diagnostics;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace Pole.PrivateAccess.Prototype.Tests.Integration;

public sealed class DatabaseTransportTests(DatabaseFixture databases) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task Postgres_transactions_and_binary_payload_cross_the_local_forward()
    {
        await using var tunnel = PostgresTunnel();
        await using var connection = await OpenPostgresAsync(tunnel);
        await ExecuteAsync(connection, "CREATE TEMP TABLE transport_test (id integer, payload bytea)", tunnel.Token);
        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        await using (var transaction = await connection.BeginTransactionAsync(tunnel.Token))
        {
            await using var insert = new NpgsqlCommand("INSERT INTO transport_test VALUES (1, $1)", connection, transaction);
            insert.Parameters.AddWithValue(payload);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(tunnel.Token));
            await transaction.CommitAsync(tunnel.Token);
        }
        await using (var transaction = await connection.BeginTransactionAsync(tunnel.Token))
        {
            await ExecuteAsync(connection, "INSERT INTO transport_test VALUES (2, NULL)", tunnel.Token);
            await transaction.RollbackAsync(tunnel.Token);
        }
        await using var read = new NpgsqlCommand("SELECT payload FROM transport_test", connection);
        Assert.Equal(payload, Assert.IsType<byte[]>(await read.ExecuteScalarAsync(tunnel.Token)));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM transport_test", tunnel.Token));
    }

    [Fact]
    public async Task Redis_binary_commands_and_blocking_pop_work_and_revoke_interrupts_an_indefinite_pop()
    {
        await using var tunnel = RedisTunnel();
        await using var consumer = await OpenRedisAsync(tunnel);
        await using var producer = await OpenRedisAsync(tunnel);
        var key = Guid.NewGuid().ToString("N");
        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);
        Assert.True(await producer.GetDatabase().StringSetAsync(key, payload));
        Assert.Equal(payload, (byte[]?)await consumer.GetDatabase().StringGetAsync(key));
        var list = key + "-list";
        var pop = consumer.GetDatabase().ExecuteAsync("BLPOP", list, 0);
        await Task.Delay(200, tunnel.Token);
        Assert.False(pop.IsCompleted);
        await producer.GetDatabase().ListLeftPushAsync(list, "delivered");
        var result = (RedisResult[])(await pop.WaitAsync(TimeSpan.FromSeconds(5)))!;
        Assert.Equal("delivered", result[1].ToString());

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        consumer.ConnectionFailed += (_, _) => disconnected.TrySetResult();
        var blocked = consumer.GetDatabase().ExecuteAsync("BLPOP", list, 0);
        await Task.Delay(200, tunnel.Token);
        Assert.False(blocked.IsCompleted);
        tunnel.Authority.Enabled = false;
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<RedisException>(async () => await blocked.WaitAsync(TimeSpan.FromSeconds(6)));
        await Assert.ThrowsAnyAsync<RedisException>(() => OpenRedisAsync(tunnel));
    }

    [Theory]
    [InlineData(RedisProtocol.Resp2)]
    [InlineData(RedisProtocol.Resp3)]
    public async Task Redis_pubsub_delivers_and_the_subscription_connection_closes_after_revoke(RedisProtocol protocol)
    {
        await using var tunnel = RedisTunnel();
        await using var subscriber = await OpenRedisAsync(tunnel, protocol);
        await using var publisher = await OpenRedisAsync(tunnel, protocol);
        var channel = RedisChannel.Literal(Guid.NewGuid().ToString("N"));
        var subscription = await subscriber.GetSubscriber().SubscribeAsync(channel);
        Assert.Equal(1, await publisher.GetSubscriber().PublishAsync(channel, "message"));
        Assert.Equal("message", (await subscription.ReadAsync(tunnel.Token)).Message.ToString());
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ConnectionFailed += (_, args) =>
        {
            if (args.ConnectionType == (protocol == RedisProtocol.Resp2 ? ConnectionType.Subscription : ConnectionType.Interactive))
                disconnected.TrySetResult();
        };
        tunnel.Authority.Enabled = false;
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<RedisException>(() => OpenRedisAsync(tunnel));
    }

    [Fact]
    public async Task Postgres_and_Redis_connections_survive_many_renewals_and_the_idle_registration_timeout()
    {
        await using var postgresTunnel = PostgresTunnel();
        await using var redisTunnel = RedisTunnel();
        await using var postgres = await OpenPostgresAsync(postgresTunnel);
        await using var redis = await OpenRedisAsync(redisTunnel);
        var postgresId = await ScalarAsync(postgres, "SELECT pg_backend_pid()", postgresTunnel.Token);
        var redisId = (long)await redis.GetDatabase().ExecuteAsync("CLIENT", "ID");
        var elapsed = Stopwatch.StartNew();
        var probes = 0;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(35))
        {
            Assert.Equal(postgresId, await ScalarAsync(postgres, "SELECT pg_backend_pid()", postgresTunnel.Token));
            Assert.Equal(redisId, (long)await redis.GetDatabase().ExecuteAsync("CLIENT", "ID"));
            probes++;
            await Task.Delay(250, postgresTunnel.Token);
        }
        Assert.True(probes >= 20);
        await Task.Delay(TunnelFixture.LeaseDuration * 3, postgresTunnel.Token);
        Assert.Equal(postgresId, await ScalarAsync(postgres, "SELECT pg_backend_pid()", postgresTunnel.Token));
        Assert.Equal(redisId, (long)await redis.GetDatabase().ExecuteAsync("CLIENT", "ID"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_concurrent_Postgres_session_loses_access_on_revoke_or_expiry(bool expire)
    {
        await using var tunnel = PostgresTunnel();
        var connections = new List<NpgsqlConnection>();
        try
        {
            for (var index = 0; index < 4; index++)
                connections.Add(await OpenPostgresAsync(tunnel));
            var counts = new int[connections.Count];
            var stopped = connections.Select((connection, index) => RunQueriesAsync(connection, index)).ToArray();
            var startup = Stopwatch.StartNew();
            while (counts.Any(count => count < 10))
            {
                Assert.True(startup.Elapsed < TimeSpan.FromSeconds(10), "Every concurrent session must make progress before revocation.");
                await Task.Delay(20, tunnel.Token);
            }
            Assert.All(stopped, task => Assert.False(task.IsCompleted));
            if (expire)
                tunnel.Authority.Expire();
            else
                tunnel.Authority.Enabled = false;
            await Task.WhenAll(stopped).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => OpenPostgresAsync(tunnel));

            async Task RunQueriesAsync(NpgsqlConnection connection, int index)
            {
                try
                {
                    while (true)
                    {
                        Assert.Equal(1, await ScalarAsync(connection, "SELECT 1", tunnel.Token));
                        Interlocked.Increment(ref counts[index]);
                        await Task.Delay(10, tunnel.Token);
                    }
                }
                catch (NpgsqlException) { }
            }
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("broker")]
    [InlineData("connector")]
    [InlineData("database")]
    public async Task Fresh_Postgres_sessions_recover_after_restart_without_resuming_old_sessions(string component)
    {
        await using var tunnel = PostgresTunnel();
        await using var old = await OpenPostgresAsync(tunnel);
        Assert.Equal(1, await ScalarAsync(old, "SELECT 1", tunnel.Token));
        switch (component)
        {
            case "broker": await tunnel.RestartBrokerAsync(); break;
            case "connector": await tunnel.RestartConnectorsAsync(); break;
            case "database":
                await databases.Postgres.StopAsync(tunnel.Token);
                await databases.Postgres.StartAsync(tunnel.Token);
                break;
        }
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => ScalarAsync(old, "SELECT 1", tunnel.Token));
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await using var fresh = await OpenPostgresAsync(tunnel);
                Assert.Equal(1, await ScalarAsync(fresh, "SELECT 1", tunnel.Token));
                break;
            }
            catch (NpgsqlException) when (elapsed.Elapsed < TimeSpan.FromSeconds(12))
            {
                await Task.Delay(100, tunnel.Token);
            }
        }
    }

    private TunnelFixture RedisTunnel() => new(databases.Redis.Hostname, databases.Redis.GetMappedPublicPort(6379));

    private static Task<ConnectionMultiplexer> OpenRedisAsync(TunnelFixture tunnel, RedisProtocol? protocol = null) =>
        ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { "127.0.0.1", tunnel.Port } },
            Protocol = protocol,
            AbortOnConnectFail = true,
            ConnectRetry = 0,
            ConnectTimeout = 1500,
            AsyncTimeout = 5000,
            SyncTimeout = 5000
        });

    private TunnelFixture PostgresTunnel() => new(databases.Postgres.Hostname, databases.Postgres.GetMappedPublicPort(5432));

    private async Task<NpgsqlConnection> OpenPostgresAsync(TunnelFixture tunnel)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = tunnel.Port,
            Username = "postgres",
            Password = databases.Password,
            Database = "postgres",
            Pooling = false,
            Timeout = 4,
            CommandTimeout = 10,
            SslMode = SslMode.Disable
        }.ConnectionString);
        try { await connection.OpenAsync(tunnel.Token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
