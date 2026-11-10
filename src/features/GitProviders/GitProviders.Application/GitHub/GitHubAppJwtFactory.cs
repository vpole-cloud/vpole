using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pole.Features.GitProviders.Application.Common;

namespace Pole.Features.GitProviders.Application.GitHub;

public delegate string CreateGitHubAppJwt();

public sealed class GitHubAppJwtFactory(
    GitHubAppOptions options,
    TimeProvider timeProvider
)
{
    public string Invoke()
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("GitHub App integration is not configured.");

        var now = timeProvider.GetUtcNow();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(options.PrivateKeyPath));

        var handler = new JsonWebTokenHandler
        {
            SetDefaultTimesOnTokenCreation = false,
        };
        var signingKey = new RsaSecurityKey(rsa)
        {
            CryptoProviderFactory = new CryptoProviderFactory
            {
                CacheSignatureProviders = false,
            },
        };
        return handler.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = options.ClientId,
                IssuedAt = now.AddSeconds(-60).UtcDateTime,
                Expires = now.AddMinutes(8).UtcDateTime,
                TokenType = "JWT",
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            });
    }
}
