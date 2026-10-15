using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pole.Features.Realtime.Application.Redis;

internal static class RealtimeRedisKeys
{
    // One Redis Cluster hash slot allows all registry changes to remain atomic.
    private const string Prefix = "{pole:realtime}:";
    public const string DataProtection = Prefix + "data-protection";
    public const string SubscriptionPrefix = Prefix + "subscription:";
    public static string Ticket(string sessionId) => Prefix + "ticket:" + sessionId;
    public static string Connection(string connectionId) => Prefix + "connection:" + connectionId;
    public static string Session(string sessionId) => Prefix + "session:" + sessionId;
    public static string Revoked(string sessionId) => Prefix + "revoked:" + sessionId;

    public static string AllTopic(string topic) => Prefix + "all-topic:" +
                                                   Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(topic)));

    public static string Topic(string topic, string? tenantId) => Prefix + "topic:" +
                                                                  Convert.ToHexString(
                                                                      SHA256.HashData(
                                                                          Encoding.UTF8.GetBytes(
                                                                              JsonSerializer.Serialize(new[]
                                                                                  { topic, tenantId }))));
}
