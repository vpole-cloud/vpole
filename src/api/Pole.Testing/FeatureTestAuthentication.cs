using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;

namespace Pole.Testing;

public sealed class FeatureTestAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "FeatureTestIdentity";
    public const string UserHeader = "X-Test-User-Id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userId) || string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim>
            { new(Claims.UserId, userId.ToString()), new(Claims.Email, TestPlatformDefaults.UserEmail) };
        if (Request.Headers["X-Test-Platform-Admin"] == "true")
            claims.Add(new Claim(Claims.Roles, Roles.Admin));
        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, Claims.Email, Claims.Roles)),
                    Scheme)));
    }
}
