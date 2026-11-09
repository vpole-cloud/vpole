using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User-Id";
    public const string EmailHeader = "X-Test-User-Email";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Test-Unauthenticated"] == "true") return Task.FromResult(AuthenticateResult.NoResult());
        if (!Request.Headers.TryGetValue(UserHeader, out var userId) || string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim>
        {
            new(Claims.UserId, userId.ToString()),
            new(Claims.Email, Request.Headers[EmailHeader].ToString())
        };

        if (Request.Headers.TryGetValue("X-Test-Platform-Admin", out var isPlatformAdmin) &&
            string.Equals(isPlatformAdmin.FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim(Claims.Roles, Roles.Admin));
        }

        var identity = new ClaimsIdentity(
            claims,
            SchemeName,
            Claims.Email,
            Claims.Roles);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
