using System.Text.Json;
using Pole.Features.Realtime.Contracts;
using StackExchange.Redis;

namespace Pole.Features.Realtime.Application.Redis;

/// <summary>Atomic, tenant-keyed subscription interests shared by all API replicas.</summary>
public sealed class RedisRealtimeSubscriptions(
    RealtimeRedisConnection connection,
    TimeProvider clock) : IRealtimeSubscriptions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Entry(
        RealtimeSubscription Subscription,
        string TopicKey,
        string ConnectionKey,
        string SessionKey,
        string RevokedKey,
        long ExpiresAt);

    // Every index expires with its last member. Removing/replacing a connection adjusts
    // all three indices in the same Lua execution, including members whose data expired.
    private const string Helpers = """
        local now = tonumber(ARGV[1])
        local dataPrefix = ARGV[2]
        local function trim(key)
            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)
            local last = redis.call('ZRANGE', key, -1, -1, 'WITHSCORES')
            if #last == 0 then redis.call('DEL', key)
            else redis.call('PEXPIREAT', key, math.floor(tonumber(last[2]))) end
        end
        local function remove(id)
            local raw = redis.call('GET', dataPrefix .. id)
            if raw then
                local entry = cjson.decode(raw)
                for _, key in ipairs({entry.topicKey, entry.connectionKey, entry.sessionKey}) do
                    redis.call('ZREM', key, id)
                    trim(key)
                end
                redis.call('DEL', dataPrefix .. id)
            end
        end
        """;

    private const string ReplaceScript = Helpers + """

        local entries = cjson.decode(ARGV[3])
        for _, entry in ipairs(entries) do
            if redis.call('EXISTS', entry.revokedKey) == 1 then return 0 end
        end
        local previous = redis.call('ZRANGE', KEYS[1], 0, -1)
        for _, id in ipairs(previous) do remove(id) end
        redis.call('DEL', KEYS[1])
        for _, entry in ipairs(entries) do
            if entry.expiresAt > now then
                local id = entry.subscription.id
                redis.call('SET', dataPrefix .. id, cjson.encode(entry), 'PXAT', entry.expiresAt)
                for _, key in ipairs({entry.topicKey, entry.connectionKey, entry.sessionKey}) do
                    redis.call('ZADD', key, entry.expiresAt, id)
                    trim(key)
                end
            end
        end
        return 1
        """;

    private const string RemoveScript = Helpers + """

        local ids
        if ARGV[3] == '' then ids = redis.call('ZRANGE', KEYS[1], 0, -1)
        else ids = cjson.decode(ARGV[3]) end
        for _, id in ipairs(ids) do
            if redis.call('ZSCORE', KEYS[1], id) then
                remove(id)
                redis.call('ZREM', KEYS[1], id)
            end
        end
        trim(KEYS[1])
        return 1
        """;

    private const string FindScript = Helpers + """

        trim(KEYS[1])
        local result = {}
        for _, id in ipairs(redis.call('ZRANGE', KEYS[1], 0, -1)) do
            local raw = redis.call('GET', dataPrefix .. id)
            if raw then table.insert(result, raw)
            else redis.call('ZREM', KEYS[1], id) end
        end
        trim(KEYS[1])
        return result
        """;

    private const string RevokeScript = Helpers + """

        redis.call('SET', KEYS[2], '1', 'PX', 300000)
        local connections = {}
        for _, id in ipairs(redis.call('ZRANGE', KEYS[1], 0, -1)) do
            local raw = redis.call('GET', dataPrefix .. id)
            if raw then
                local entry = cjson.decode(raw)
                if entry.expiresAt > now then connections[entry.subscription.connectionId] = true end
            end
            remove(id)
        end
        redis.call('DEL', KEYS[1])
        local result = {}
        for id, _ in pairs(connections) do table.insert(result, id) end
        return result
        """;

    public async Task ReplaceAsync(string connectionId, IReadOnlyList<RealtimeSubscription> subscriptions, CancellationToken cancellationToken)
    {
        if (subscriptions.Any(item => item.ConnectionId != connectionId || string.IsNullOrWhiteSpace(item.SessionId)) ||
            subscriptions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != subscriptions.Count)
            throw new InvalidOperationException("Realtime subscriptions must have distinct identities and belong to their connection.");
        var entries = subscriptions.Select(subscription => new Entry(subscription,
            RealtimeRedisKeys.Topic(subscription.Request.Topic, subscription.Request.TenantId),
            RealtimeRedisKeys.Connection(connectionId), RealtimeRedisKeys.Session(subscription.SessionId),
            RealtimeRedisKeys.Revoked(subscription.SessionId), subscription.ExpiresAtUtc.ToUnixTimeMilliseconds()));
        var result = await EvaluateAsync(ReplaceScript, [RealtimeRedisKeys.Connection(connectionId)],
            [clock.GetUtcNow().ToUnixTimeMilliseconds(), RealtimeRedisKeys.SubscriptionPrefix,
                JsonSerializer.Serialize(entries, JsonOptions)], cancellationToken);
        if ((long)result != 1) throw new RealtimeSessionUnavailableException();
    }

    public async Task RemoveAsync(string connectionId, IReadOnlyCollection<string>? subscriptionIds, CancellationToken cancellationToken) =>
        await EvaluateAsync(RemoveScript, [RealtimeRedisKeys.Connection(connectionId)],
            [clock.GetUtcNow().ToUnixTimeMilliseconds(), RealtimeRedisKeys.SubscriptionPrefix,
                subscriptionIds is null ? "" : JsonSerializer.Serialize(subscriptionIds, JsonOptions)], cancellationToken);

    public async Task<IReadOnlyList<RealtimeSubscription>> FindAsync(string topic, string? tenantId, CancellationToken cancellationToken)
    {
        var result = await EvaluateAsync(FindScript, [RealtimeRedisKeys.Topic(topic, tenantId)],
            [clock.GetUtcNow().ToUnixTimeMilliseconds(), RealtimeRedisKeys.SubscriptionPrefix], cancellationToken);
        return ((RedisResult[])result!).Select(value =>
            JsonSerializer.Deserialize<Entry>((string)value!, JsonOptions)!.Subscription).ToArray();
    }

    public async Task<IReadOnlyList<string>> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var result = await EvaluateAsync(RevokeScript,
            [RealtimeRedisKeys.Session(sessionId), RealtimeRedisKeys.Revoked(sessionId)],
            [clock.GetUtcNow().ToUnixTimeMilliseconds(), RealtimeRedisKeys.SubscriptionPrefix], cancellationToken);
        return ((RedisResult[])result!).Select(value => (string)value!).ToArray();
    }

    public Task<bool> IsSessionRevokedAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return connection.Database.KeyExistsAsync(RealtimeRedisKeys.Revoked(sessionId)).WaitAsync(cancellationToken);
    }

    private Task<RedisResult> EvaluateAsync(string script, RedisKey[] keys, RedisValue[] values, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return connection.Database.ScriptEvaluateAsync(script, keys, values).WaitAsync(cancellationToken);
    }
}
