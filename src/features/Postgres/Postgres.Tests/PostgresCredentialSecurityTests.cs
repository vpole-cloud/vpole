using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Encryption;
using Pole.Features.Postgres.Domain;
using Pole.ResultType;
using Pole.Features.Postgres.Tests;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresCredentialSecurityTests
{
    [Theory]
    [InlineData("tenant")]
    [InlineData("cluster")]
    [InlineData("field")]
    [InlineData("version")]
    public async Task EncryptionContext_RejectsIdentitySubstitution(
        string changedIdentity)
    {
        using var settings = new TemporaryEncryptionConfiguration();
        using var services = new ServiceCollection().AddEnvelopeEncryption(settings.Configuration).BuildServiceProvider();
        var encryption = services.GetRequiredService<IEnvelopeEncryptionService>();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var clusterId = ObjectId.GenerateNewId().ToString();
        var originalContext = Context(
            tenantId,
            clusterId,
            PostgresCredential.PasswordField,
            1);
        var encryptedResult = await encryption.EncryptStringAsync(
            "never-store-plaintext",
            originalContext);
        Assert.True(
            encryptedResult.TryGetValue(out var encrypted, out var encryptFailure),
            encryptFailure?.Message);

        var changedContext = changedIdentity switch
        {
            "tenant" => Context(
                ObjectId.GenerateNewId().ToString(),
                clusterId,
                PostgresCredential.PasswordField,
                1),
            "cluster" => Context(
                tenantId,
                ObjectId.GenerateNewId().ToString(),
                PostgresCredential.PasswordField,
                1),
            "field" => Context(
                tenantId,
                clusterId,
                PostgresCredential.UsernameField,
                1),
            "version" => Context(
                tenantId,
                clusterId,
                PostgresCredential.PasswordField,
                2),
            _ => throw new ArgumentOutOfRangeException(nameof(changedIdentity))
        };

        var decrypted = await encryption.DecryptStringAsync(
            encrypted!,
            changedContext);

        Assert.False(decrypted.TryGetValue(out _, out _));
    }

    private static EnvelopeEncryptionContext Context(
        string tenantId,
        string clusterId,
        string field,
        long version) =>
        EnvelopeEncryptionContext.ForTenant(
            tenantId,
            PostgresCredential.ObjectType,
            clusterId,
            field,
            version);

    private sealed class TemporaryEncryptionConfiguration : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "pole-postgres-tests",
            Guid.NewGuid().ToString("N"));

        public IConfiguration Configuration { get; }

        public TemporaryEncryptionConfiguration()
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "test.key");
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllText(path, Convert.ToBase64String(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            Configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["EnvelopeEncryption:ActiveProviderId"] = "local-key-file",
                    ["EnvelopeEncryption:LocalKeyFile:ActiveKeyId"] = "test",
                    ["EnvelopeEncryption:LocalKeyFile:ActiveKeyVersion"] = "1",
                    ["EnvelopeEncryption:LocalKeyFile:Keys:0:Id"] = "test",
                    ["EnvelopeEncryption:LocalKeyFile:Keys:0:Version"] = "1",
                    ["EnvelopeEncryption:LocalKeyFile:Keys:0:Path"] = path
                }).Build();
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
