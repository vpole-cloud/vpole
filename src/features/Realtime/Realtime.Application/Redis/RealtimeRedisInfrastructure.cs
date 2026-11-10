using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Pole.Features.Realtime.Application.Redis;

/// <summary>A dedicated infrastructure connection, separate from managed Redis workloads.</summary>
public sealed class RealtimeRedisConnection(
    IConnectionMultiplexer multiplexer
) : IDisposable
{
    public IDatabase Database => multiplexer.GetDatabase();
    public IConnectionMultiplexer Multiplexer => multiplexer;
    public void Dispose() => multiplexer.Dispose();
}

public static class RealtimeRedisInfrastructure
{
    public static IServiceCollection AddRealtimeRedis(
        this IServiceCollection services,
        IConfiguration configuration,
        bool development)
    {
        if (!configuration.GetValue<bool>("Realtime:Enabled"))
            return services;
        var connectionString = configuration.GetConnectionString("realtime");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Enabled realtime requires ConnectionStrings:realtime.");
        var certificatePath = configuration["Realtime:KeyProtectionCertificatePath"];
        if (string.IsNullOrWhiteSpace(certificatePath))
            throw new InvalidOperationException(
                development
                    ? "Enabled realtime requires a key-protection certificate. Aspire creates an ignored development certificate."
                    : "Enabled realtime requires Realtime:KeyProtectionCertificatePath with a private key.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath,
            configuration["Realtime:KeyProtectionCertificatePassword"],
            X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "The realtime key-protection certificate must contain its private key.");
        }

        services.AddSingleton(_ => certificate);
        var redisOptions = ConfigurationOptions.Parse(connectionString);
        services.TryAddSingleton(provider =>
        {
            var redis = redisOptions.Clone();
            redis.AbortOnConnectFail = false;
            redis.BacklogPolicy = BacklogPolicy.FailFast;
            redis.ConnectTimeout = 5000;
            redis.AsyncTimeout = 3000;
            redis.SyncTimeout = 3000;
            return new RealtimeRedisConnection(ConnectionMultiplexer.Connect(redis));
        });
        services.AddDataProtection().SetApplicationName("Pole").ProtectKeysWithCertificate(certificate);
        services.AddOptions<KeyManagementOptions>()
            .Configure<RealtimeRedisConnection, X509Certificate2>((
                options, redis,
                _) => options.XmlRepository = new RedisXmlRepository(
                () => redis.Database,
                RealtimeRedisKeys.DataProtection));
        services.AddSignalR().AddStackExchangeRedis();
        services.AddOptions<RedisOptions>()
            .Configure<RealtimeRedisConnection>((options, redis) =>
            {
                options.Configuration.ChannelPrefix = RedisChannel.Literal("Pole.Realtime");
                options.ConnectionFactory = _ => Task.FromResult(redis.Multiplexer);
            });
        services.AddSingleton<IRealtimeTicketStore, RedisRealtimeTicketStore>();
        services.AddSingleton<IRealtimeSubscriptions, RedisRealtimeSubscriptions>();
        return services;
    }
}
