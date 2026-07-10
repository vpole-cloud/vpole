using System.Security.Cryptography;
using System.Text;

namespace Pole.Features.GitProviders.Application.GitHub;

public interface IGitHubSetupTokenService
{
    string CreateState();
    string Hash(string value);
    string CreateCodeVerifier();
    string CreateCodeChallenge(string verifier);
}

public sealed class GitHubSetupTokenService : IGitHubSetupTokenService
{
    public string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public string CreateCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
