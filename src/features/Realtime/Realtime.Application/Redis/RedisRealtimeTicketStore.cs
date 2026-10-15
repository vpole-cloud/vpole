using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Pole.ASP.Helpers;
using StackExchange.Redis;

namespace Pole.Features.Realtime.Application.Redis;

/// <summary>Opaque session identifiers refer to short-lived, protected framework tickets.</summary>
public sealed class RedisRealtimeTicketStore(
    RealtimeRedisConnection connection,
    IDataProtectionProvider protection,
    RealtimeOptions options,
    TimeProvider clock) : IRealtimeTicketStore
{
    private readonly IDataProtector _protector = protection.CreateProtector(
        "Pole.Realtime.RedisTicketStore",
        RealtimeFeature.CookieScheme,
        "v1");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var sessionId = GetSessionId(ticket);
        var sanitized = Sanitize(ticket);
        var stored = await connection.Database.StringSetAsync(RealtimeRedisKeys.Ticket(sessionId),
            Protect(sanitized),
            sanitized.Properties.ExpiresUtc!.Value - clock.GetUtcNow(),
            When.NotExists);
        if (!stored)
            throw new InvalidOperationException("The realtime session already exists.");
        return sessionId;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        if (GetSessionId(ticket) != key)
            throw new InvalidOperationException("The realtime session identity cannot change.");
        var sanitized = Sanitize(ticket);
        // XX fences renewal against concurrent logout; renewal never recreates a removed ticket.
        var renewed = await connection.Database.StringSetAsync(RealtimeRedisKeys.Ticket(key),
            Protect(sanitized),
            sanitized.Properties.ExpiresUtc!.Value - clock.GetUtcNow(),
            When.Exists);
        if (!renewed)
            throw new RealtimeSessionUnavailableException();
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var value = await connection.Database.StringGetAsync(RealtimeRedisKeys.Ticket(key));
        if (value.IsNull)
            return null;
        AuthenticationTicket? ticket;
        try
        {
            ticket = TicketSerializer.Default.Deserialize(_protector.Unprotect((byte[])value!));
        }
        catch (CryptographicException)
        {
            await RemoveAsync(key);
            return null;
        }

        if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow() ||
            ticket.Principal.FindFirstValue(RealtimeFeature.SessionClaim) != key)
        {
            await RemoveAsync(key);
            return null;
        }

        return ticket;
    }

    public Task RemoveAsync(string key) => connection.Database.KeyDeleteAsync(RealtimeRedisKeys.Ticket(key));

    private byte[] Protect(AuthenticationTicket ticket) =>
        _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    private AuthenticationTicket Sanitize(AuthenticationTicket ticket)
    {
        var now = clock.GetUtcNow();
        var expiry = ticket.Properties.ExpiresUtc ??
                     throw new InvalidOperationException("Realtime tickets require an expiry.");
        var maximum = now + options.SessionLifetime;
        if (expiry > maximum)
            expiry = maximum;
        if (expiry <= now)
            throw new InvalidOperationException("An expired realtime ticket cannot be stored.");
        var claims = ticket.Principal.Claims.Where(claim => claim.Type is
            "iss" or Claims.UserId or "sid" or Claims.Roles or RealtimeFeature.SessionClaim);
        var principal =
            new ClaimsPrincipal(new ClaimsIdentity(claims, RealtimeFeature.CookieScheme, Claims.UserId, Claims.Roles));
        return new AuthenticationTicket(principal,
            new AuthenticationProperties
            {
                IssuedUtc = ticket.Properties.IssuedUtc ?? now,
                ExpiresUtc = expiry,
                AllowRefresh = false,
                IsPersistent = false
            },
            RealtimeFeature.CookieScheme);
    }

    private static string GetSessionId(AuthenticationTicket ticket)
    {
        var ids = ticket.Principal.FindAll(RealtimeFeature.SessionClaim).ToArray();
        if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0].Value))
            throw new InvalidOperationException("Realtime tickets require one opaque session identifier.");
        return ids[0].Value;
    }
}
