using Pole.Cli.Authentication;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class CliTokenStoreTests
{
    [Fact]
    public async Task Token_file_is_private_and_cache_is_bound_to_server_authority()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pole-cli-token-test-" + Guid.NewGuid().ToString("N"));
        var store = new CliTokenStore(Path.Combine(directory, "cli", "tokens.json"));
        var issuer = new Uri("https://identity.example/realms/pole");
        var server = new Uri("https://pole.example:8443/");
        var tokens = new OidcTokens("access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddMinutes(5));
        try
        {
            await store.SaveAsync(issuer, "pole.cli", server, tokens, CancellationToken.None);

            var loaded = await store.LoadAsync(issuer, "pole.cli", server, CancellationToken.None);
            Assert.Equal(tokens, loaded);
            Assert.Null(await store.LoadAsync(issuer, "pole.cli", new Uri("https://other.example:8443/"), CancellationToken.None));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.Combine(directory, "cli")));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(directory, "cli", "tokens.json")));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Token_store_rejects_reparse_point_token_files()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), "pole-cli-token-link-test-" + Guid.NewGuid().ToString("N"));
        var cliDirectory = Path.Combine(directory, "cli");
        Directory.CreateDirectory(cliDirectory);
        var target = Path.Combine(directory, "outside.json");
        await File.WriteAllTextAsync(target, "{}");
        var tokenPath = Path.Combine(cliDirectory, "tokens.json");
        File.CreateSymbolicLink(tokenPath, target);
        try
        {
            var store = new CliTokenStore(tokenPath);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.LoadAsync(new Uri("https://identity.example/realms/pole"), "pole.cli", new Uri("https://pole.example/"), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
