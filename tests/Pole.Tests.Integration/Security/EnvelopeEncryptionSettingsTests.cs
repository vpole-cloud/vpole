using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pole.Encryption;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.Security;

public sealed class EnvelopeEncryptionSettingsTests
{
    [Fact]
    public async Task ValidLocalKeyFileConfigurationStartsWithoutAnExternalService()
    {
        using var key = TestKeyFile.Create();
        using var host = CreateHost(Configuration(key.Path));

        await host.StartAsync();

        var service = host.Services.GetRequiredService<IEnvelopeEncryptionService>();
        var context = EnvelopeEncryptionContext.ForPlatform("test", "settings", "1", "value", 1);
        var encrypted = await service.EncryptStringAsync("configuration-round-trip", context);
        Assert.True(encrypted.TryGetValue(out var value, out _));
        var decrypted = await service.DecryptStringAsync(value, context);
        Assert.True(decrypted.TryGetValue(out var plaintext, out _));
        Assert.Equal("configuration-round-trip", plaintext);
    }

    [Fact]
    public async Task MissingKeyFailsStartupAndDoesNotGenerateAReplacement()
    {
        using var directory = TestKeyFile.CreateDirectoryOnly();
        var missingPath = Path.Combine(directory.Path, "missing.key");
        using var host = CreateHost(Configuration(missingPath));

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.False(File.Exists(missingPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task InvalidKeyFailsStartupWithoutLeakingKeyFileContents()
    {
        const string sentinel = "SENTINEL-KEY-MATERIAL-MUST-NOT-LEAK";
        using var key = TestKeyFile.CreateWithContents(sentinel);
        using var host = CreateHost(Configuration(key.Path));

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key.Path, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownProviderFailsStartup()
    {
        using var key = TestKeyFile.Create();
        var values = ConfigurationValues(key.Path);
        values["EnvelopeEncryption:ActiveProviderId"] = "unknown-provider";
        using var host = CreateHost(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task CustomProviderCanOptOutOfLocalKeyFileConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnvelopeEncryption:ActiveProviderId"] = "test-provider"
            })
            .Build();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IKeyEncryptionProvider, TestKeyEncryptionProvider>();
                services.AddEnvelopeEncryption(configuration, includeLocalKeyFileProvider: false);
            })
            .Build();

        await host.StartAsync();

        var service = host.Services.GetRequiredService<IEnvelopeEncryptionService>();
        Assert.Equal("test-provider", service.ActiveKey.ProviderId);
    }

    private static IHost CreateHost(IConfiguration configuration) =>
        new HostBuilder()
            .ConfigureServices(services => services.AddEnvelopeEncryption(configuration))
            .Build();

    private static IConfiguration Configuration(string path) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(ConfigurationValues(path))
            .Build();

    private static Dictionary<string, string?> ConfigurationValues(string path) => new()
    {
        ["EnvelopeEncryption:ActiveProviderId"] = LocalKeyFileKeyEncryptionProvider.StableProviderId,
        ["EnvelopeEncryption:LocalKeyFile:ActiveKeyId"] = "settings-test",
        ["EnvelopeEncryption:LocalKeyFile:ActiveKeyVersion"] = "1",
        ["EnvelopeEncryption:LocalKeyFile:Keys:0:Id"] = "settings-test",
        ["EnvelopeEncryption:LocalKeyFile:Keys:0:Version"] = "1",
        ["EnvelopeEncryption:LocalKeyFile:Keys:0:Path"] = path
    };

    private sealed class TestKeyFile : IDisposable
    {
        private TestKeyFile(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        private string Directory { get; }
        public string Path { get; }

        public static TestKeyFile Create()
        {
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                return CreateWithContents(Convert.ToBase64String(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        public static TestKeyFile CreateWithContents(string contents)
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pole-encryption-settings",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "key-v1.key");
            File.WriteAllText(path, contents);
            return new TestKeyFile(directory, path);
        }

        public static TestDirectory CreateDirectoryOnly() => new();

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pole-encryption-settings",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }

    private sealed class TestKeyEncryptionProvider : IKeyEncryptionProvider
    {
        public string ProviderId => "test-provider";
        public KeyEncryptionKeyDescriptor ActiveKey { get; } = new(
            new KeyEncryptionKeyIdentity("test-key", 1),
            "test-wrap",
            1);

        public ValueTask<Result<WrappedDataEncryptionKey, DataEncryptionKeyWrappingFailed>> WrapKeyAsync(
            KeyEncryptionKeyDescriptor key,
            ReadOnlyMemory<byte> dataEncryptionKey,
            ReadOnlyMemory<byte> associatedData,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<byte[], DataEncryptionKeyUnwrappingFailed>> UnwrapKeyAsync(
            KeyEncryptionKeyIdentity key,
            string algorithm,
            int algorithmVersion,
            ReadOnlyMemory<byte> wrappedDataEncryptionKey,
            ReadOnlyMemory<byte> nonce,
            ReadOnlyMemory<byte> authenticationTag,
            ReadOnlyMemory<byte> associatedData,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
