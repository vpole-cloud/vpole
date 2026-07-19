using Pole.ResultType;

namespace Pole.Encryption.Tests;

public sealed class EnvelopeEncryptionDiagnosticsTests
{
    [Fact]
    public async Task Decryption_failure_does_not_disclose_sensitive_value_or_context()
    {
        using var files = new TemporaryKeyDirectory();
        const string plaintext = "never-log-this-secret-value";
        const string tenantId = "sensitive-tenant-id";
        const string objectId = "sensitive-object-id";
        var context = EnvelopeEncryptionContext.ForTenant(
            tenantId, "git-provider", objectId, "sensitive-field-name", 1);
        using var encryption = TestEncryptionService.Create(
            "primary", 1, new TestKey("primary", 1, files.WriteKey("primary.key")));
        var encryptedResult = await encryption.Service.EncryptStringAsync(plaintext, context);
        Assert.True(encryptedResult.TryGetValue(out var encrypted, out var encryptionFailure), encryptionFailure?.Message);
        var clone = EncryptionTestData.CloneEnvelope(encrypted);
        var tampered = clone with
        {
            AuthenticationTag = EncryptionTestData.FlipFirstByte(clone.AuthenticationTag)
        };

        var result = await encryption.Service.DecryptStringAsync(tampered, context);

        Assert.False(result.TryGetValue(out _, out var failure));
        var internalFailure = Assert.IsType<InternalFailure>(failure);
        var diagnostic = internalFailure.Message + " " + internalFailure.Details;
        Assert.DoesNotContain(plaintext, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(objectId, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-field-name", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(tampered.Ciphertext), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(tampered.WrappedDataEncryptionKey), diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Envelope_and_wrapped_key_string_representations_are_redacted()
    {
        var envelope = new EncryptedValue(
            1,
            "provider",
            "key",
            1,
            "algorithm",
            1,
            [11, 12],
            [13, 14],
            [15, 16],
            "content-algorithm",
            1,
            [21, 22],
            [23, 24],
            [25, 26]);
        var wrappedKey = new WrappedDataEncryptionKey([31, 32], [33, 34], [35, 36]);

        var envelopeText = envelope.ToString();
        var wrappedKeyText = wrappedKey.ToString();

        Assert.DoesNotContain("11", envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain("21", envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain("31", wrappedKeyText, StringComparison.Ordinal);
        Assert.Contains("REDACTED", wrappedKeyText, StringComparison.Ordinal);
    }
}
