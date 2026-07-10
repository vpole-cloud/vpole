using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

public sealed class GitHubSecurityTests
{
    [Fact]
    public void SetupTokens_UseRandomStateHashingAndS256Pkce()
    {
        var service = new GitHubSetupTokenService();

        var firstState = service.CreateState();
        var secondState = service.CreateState();
        var verifier = service.CreateCodeVerifier();

        Assert.NotEqual(firstState, secondState);
        Assert.Equal(43, firstState.Length);
        Assert.Equal(64, verifier.Length);
        Assert.Equal(
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            service.CreateCodeChallenge(verifier));
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(firstState))),
            service.Hash(firstState));
        Assert.DoesNotContain(firstState, service.Hash(firstState), StringComparison.Ordinal);
    }

    [Fact]
    public void AppJwt_IsRs256SignedAndUsesRequiredGitHubClaims()
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var keyPath = Path.Combine(Path.GetTempPath(), $"pole-github-app-{Guid.NewGuid():N}.pem");
        using var rsa = RSA.Create(2048);
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        try
        {
            var options = new GitHubAppOptions
            {
                Enabled = true,
                ClientId = "Iv-test-client-id",
                ClientSecret = "not-used-for-jwt",
                AppSlug = "pole-test",
                PrivateKeyPath = keyPath,
                PublicBaseUrl = "https://pole.test"
            };
            var token = new GitHubAppJwtFactory(options, new FixedTimeProvider(now)).Create();
            var parts = token.Split('.');

            Assert.Equal(3, parts.Length);
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
            Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
            Assert.Equal("Iv-test-client-id", payload.RootElement.GetProperty("iss").GetString());
            Assert.Equal(now.AddSeconds(-60).ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
            Assert.Equal(now.AddMinutes(8).ToUnixTimeSeconds(), payload.RootElement.GetProperty("exp").GetInt64());
            Assert.False(payload.RootElement.TryGetProperty("nbf", out _));
            Assert.True(payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64() < 600);
            Assert.True(rsa.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                Decode(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
