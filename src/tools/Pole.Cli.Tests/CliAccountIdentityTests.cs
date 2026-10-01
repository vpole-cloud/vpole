using System.Text;
using Pole.Cli.Authentication;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class CliAccountIdentityTests
{
    [Fact]
    public void Account_key_is_bound_to_server_issuer_and_subject()
    {
        var token = Jwt("account-one");
        var issuer = new Uri("https://identity.example/realms/pole");
        var server = new Uri("https://pole.example/");

        var key = CliAccountIdentity.GetAccountKey(token, issuer, server);

        Assert.NotEqual(key, CliAccountIdentity.GetAccountKey(Jwt("account-two"), issuer, server));
        Assert.NotEqual(key, CliAccountIdentity.GetAccountKey(token, issuer, new Uri("https://other-pole.example/")));
        Assert.NotEqual(key, CliAccountIdentity.GetAccountKey(token, new Uri("https://other-identity.example/"), server));
    }

    private static string Jwt(string subject)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"sub\":\"{subject}\"}}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"header.{payload}.signature";
    }
}
