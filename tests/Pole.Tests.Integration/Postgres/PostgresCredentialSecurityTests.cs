using MongoDB.Bson;
using Pole.Encryption;
using Pole.Features.Postgres.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

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
        var encryption = TestPostgresEnvelopeEncryption.Create();
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
}
