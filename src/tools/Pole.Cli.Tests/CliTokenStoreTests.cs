using Pole.Cli.Authentication;
using Xunit;

namespace Pole.Cli.Tests;

public sealed class CliTokenStoreTests
{
    [Fact]
    public async Task Server_profile_is_saved_with_private_permissions_and_restored_for_login()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pole-cli-profile-test-" + Guid.NewGuid().ToString("N"));
        var store = new CliTokenStore(Path.Combine(directory, "cli", "tokens.json"));
        var server = new Uri("https://pole.example:8443/");
        var identity = new PoleServerIdentity(new Uri("https://identity.example/realms/pole"), "pole.cli");
        try
        {
            await store.SaveProfileAsync(server, identity, CancellationToken.None);

            var loaded = await store.LoadProfileAsync(CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(server, loaded.Value.Server);
            Assert.Equal(identity, loaded.Value.Identity);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.Combine(directory, "cli")));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(directory, "cli", "server.json")));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Active_tenant_is_stored_per_account_and_server_profile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pole-cli-tenant-context-test-" + Guid.NewGuid().ToString("N"));
        var store = new CliTokenStore(Path.Combine(directory, "cli", "tokens.json"));
        var first = new PoleServerIdentity(new Uri("https://identity.example/realms/pole"), "pole.cli");
        var second = new PoleServerIdentity(new Uri("https://identity.example/realms/pole"), "pole.cli");
        try
        {
            await store.SaveProfileAsync(new Uri("https://pole-one.example/"), first, CancellationToken.None);
            await store.SaveActiveTenantAsync("account-one", "tenant-one", CancellationToken.None);
            await store.SaveActiveTenantAsync("account-two", "tenant-two", CancellationToken.None);
            await store.SaveProfileAsync(new Uri("https://pole-two.example/"), second, CancellationToken.None);

            Assert.Equal("tenant-one", await store.GetActiveTenantAsync("account-one", CancellationToken.None));
            Assert.Equal("tenant-two", await store.GetActiveTenantAsync("account-two", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

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
