using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Mediator;

namespace Pole.Features.Realtime.Application.Endpoints;

public sealed record CreateRealtimeSessionRequest : IMediatorRequest<CreateRealtimeSessionOutcome>;

public sealed record RealtimeSessionCreated(DateTimeOffset ExpiresAtUtc);

public sealed record RealtimeSessionIdentityUnavailable;

public sealed record RealtimeSessionAccountMismatch;

public union CreateRealtimeSessionOutcome(
    RealtimeSessionCreated,
    RealtimeSessionIdentityUnavailable,
    RealtimeSessionAccountMismatch);

public sealed class CreateRealtimeSessionHandler(
    IHttpContextAccessor contextAccessor,
    RealtimeOptions options,
    TimeProvider clock) : IMediatorOutcomeHandler<CreateRealtimeSessionRequest, CreateRealtimeSessionOutcome>
{
    public async Task<CreateRealtimeSessionOutcome> HandleAsync(CreateRealtimeSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = contextAccessor.HttpContext!;
        var principal = context.User;
        var userId = principal.FindFirstValue(Claims.UserId);
        var issuer = principal.FindFirstValue("iss");
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(issuer) ||
            !long.TryParse(principal.FindFirstValue("exp"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiresSeconds) ||
            expiresSeconds < 0 || expiresSeconds > 253402300799)
            return new RealtimeSessionIdentityUnavailable();
        var expires = DateTimeOffset.FromUnixTimeSeconds(expiresSeconds);
        if (expires <= clock.GetUtcNow())
            return new RealtimeSessionIdentityUnavailable();
        var maximum = clock.GetUtcNow() + options.SessionLifetime;
        if (expires > maximum)
            expires = maximum;

        var previous = await context.AuthenticateAsync(RealtimeFeature.CookieScheme);
        cancellationToken.ThrowIfCancellationRequested();
        if (expires <= clock.GetUtcNow())
            return new RealtimeSessionIdentityUnavailable();
        if (previous.Succeeded && (previous.Principal.FindFirstValue(Claims.UserId) != userId ||
                                   previous.Principal.FindFirstValue("iss") != issuer))
            return new RealtimeSessionAccountMismatch();
        var sessionId = previous.Succeeded ? previous.Principal.FindFirstValue(RealtimeFeature.SessionClaim) : null;
        sessionId ??= Guid.NewGuid().ToString("N");
        // Deliberately allowlist identity claims. Never persist SaveTokens or the raw JWT.
        var claims = principal.Claims.Where(claim => claim.Type is Claims.UserId or "iss" or "sid" or Claims.Roles)
            .ToList();
        claims.Add(new Claim(RealtimeFeature.SessionClaim, sessionId));
        var cookiePrincipal = new ClaimsPrincipal(new ClaimsIdentity(claims, RealtimeFeature.CookieScheme));
        try
        {
            await context.SignInAsync(RealtimeFeature.CookieScheme,
                cookiePrincipal,
                new AuthenticationProperties
                {
                    ExpiresUtc = expires,
                    IssuedUtc = clock.GetUtcNow(),
                    IsPersistent = false,
                    AllowRefresh = false
                });
        }
        catch (RealtimeSessionUnavailableException)
        {
            return new RealtimeSessionIdentityUnavailable();
        }

        if (cancellationToken.IsCancellationRequested || expires <= clock.GetUtcNow())
        {
            // A slow ticket write must not return a positive expired session. Remove only
            // this transport cookie; natural expiry does not create an explicit logout marker.
            await context.SignOutAsync(RealtimeFeature.CookieScheme);
            cancellationToken.ThrowIfCancellationRequested();
            return new RealtimeSessionIdentityUnavailable();
        }

        return new RealtimeSessionCreated(expires);
    }
}

public static class CreateRealtimeSessionEndpoint
{
    public static IEndpointRouteBuilder MapCreateRealtimeSession(this IEndpointRouteBuilder endpoints)
    {
        var enabled = endpoints.ServiceProvider.GetRequiredService<RealtimeOptions>().Enabled;
        var endpoint = endpoints.MapPost("/session",
            async (HttpContext context) =>
            {
                if (!enabled)
                    return Results.NotFound();
                var mediator = context.RequestServices.GetRequiredService<IMediator>();
                var result = await mediator.Send(new CreateRealtimeSessionRequest(),
                    cancellationToken: context.RequestAborted);
                return result.ToHttpResult(outcome => outcome switch
                {
                    RealtimeSessionCreated created => Results.Ok(created),
                    RealtimeSessionIdentityUnavailable => Results.Unauthorized(),
                    RealtimeSessionAccountMismatch => Results.Conflict(new
                    {
                        code = "realtime.session.account-mismatch"
                    })
                });
            }).WithoutTenantContext();
        if (enabled)
            endpoint.RequireAuthorization(policy =>
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser());
        else
            endpoint.AllowAnonymous();
        return endpoints;
    }
}
