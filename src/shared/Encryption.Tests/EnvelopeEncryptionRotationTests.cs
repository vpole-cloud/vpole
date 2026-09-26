
namespace Pole.Encryption.Tests;

public sealed class EnvelopeEncryptionRotationTests
{
    [Fact]
    public async Task Retaining_old_key_allows_transition_decryption_and_rewrap()
    {
        using var files = new TemporaryKeyDirectory();
        var oldKey = new TestKey("primary", 1, files.WriteKey("primary-v1.key"));
        var newKey = new TestKey("primary", 2, files.WriteKey("primary-v2.key"));
        using var oldService = TestEncryptionService.Create("primary", 1, oldKey);
        using var transitionService = TestEncryptionService.Create("primary", 2, oldKey, newKey);
        var original = await EncryptAsync(oldService.Service, "rotate me");

        var transitionDecryption = await transitionService.Service.DecryptStringAsync(
            original, EncryptionTestData.TenantContext());
        var rewrapResult = await transitionService.Service.RewrapAsync(
            original, EncryptionTestData.TenantContext());

        var plaintext = Assert.IsType<string>(transitionDecryption.Value);
        Assert.Equal("rotate me", plaintext);
        var rewrapped = Assert.IsType<EncryptedValue>(rewrapResult.Value);
        Assert.Equal(2, rewrapped.KeyEncryptionKeyVersion);
    }

    [Fact]
    public async Task Rewrap_preserves_payload_and_moves_decryption_to_new_key_only()
    {
        using var files = new TemporaryKeyDirectory();
        var oldKey = new TestKey("primary", 1, files.WriteKey("primary-v1.key"));
        var newKey = new TestKey("primary", 2, files.WriteKey("primary-v2.key"));
        using var oldOnly = TestEncryptionService.Create("primary", 1, oldKey);
        using var transition = TestEncryptionService.Create("primary", 2, oldKey, newKey);
        using var newOnly = TestEncryptionService.Create("primary", 2, newKey);
        var original = await EncryptAsync(oldOnly.Service, "rotate me");

        var result = await transition.Service.RewrapAsync(original, EncryptionTestData.TenantContext());

        var rewrapped = Assert.IsType<EncryptedValue>(result.Value);
        Assert.True(original.Ciphertext.SequenceEqual(rewrapped.Ciphertext));
        Assert.True(original.Nonce.SequenceEqual(rewrapped.Nonce));
        Assert.True(original.AuthenticationTag.SequenceEqual(rewrapped.AuthenticationTag));
        Assert.False(original.WrappedDataEncryptionKey.SequenceEqual(rewrapped.WrappedDataEncryptionKey));
        var newOnlyResult = await newOnly.Service.DecryptStringAsync(rewrapped, EncryptionTestData.TenantContext());
        var plaintext = Assert.IsType<string>(newOnlyResult.Value);
        Assert.Equal("rotate me", plaintext);
        var oldOnlyResult = await oldOnly.Service.DecryptStringAsync(rewrapped, EncryptionTestData.TenantContext());
        Assert.IsType<EnvelopeDecryptionFailed>(oldOnlyResult.Value);
    }

    [Fact]
    public async Task Rewrap_is_idempotent_when_envelope_already_uses_active_key()
    {
        using var files = new TemporaryKeyDirectory();
        var key = new TestKey("primary", 2, files.WriteKey("primary-v2.key"));
        using var encryption = TestEncryptionService.Create("primary", 2, key);
        var original = await EncryptAsync(encryption.Service, "already current");

        var result = await encryption.Service.RewrapAsync(original, EncryptionTestData.TenantContext());

        var rewrapped = Assert.IsType<EncryptedValue>(result.Value);
        Assert.Same(original, rewrapped);
    }

    [Fact]
    public async Task Rewrap_does_not_decrypt_or_rewrite_a_tampered_payload()
    {
        using var files = new TemporaryKeyDirectory();
        var oldKey = new TestKey("primary", 1, files.WriteKey("primary-v1.key"));
        var newKey = new TestKey("primary", 2, files.WriteKey("primary-v2.key"));
        using var oldOnly = TestEncryptionService.Create("primary", 1, oldKey);
        using var transition = TestEncryptionService.Create("primary", 2, oldKey, newKey);
        var encrypted = await EncryptAsync(oldOnly.Service, "secret");
        var tamperedCiphertext = encrypted.Ciphertext.ToArray();
        tamperedCiphertext[0] ^= 0x01;
        var tampered = encrypted with { Ciphertext = tamperedCiphertext };

        var rewrapResult = await transition.Service.RewrapAsync(
            tampered,
            EncryptionTestData.TenantContext());

        var rewrapped = Assert.IsType<EncryptedValue>(rewrapResult.Value);
        Assert.True(tampered.Ciphertext.SequenceEqual(rewrapped.Ciphertext));
        Assert.True(tampered.Nonce.SequenceEqual(rewrapped.Nonce));
        Assert.True(tampered.AuthenticationTag.SequenceEqual(rewrapped.AuthenticationTag));
        var decryptResult = await transition.Service.DecryptStringAsync(
            rewrapped,
            EncryptionTestData.TenantContext());
        Assert.IsType<EnvelopeDecryptionFailed>(decryptResult.Value);
    }

    [Fact]
    public async Task Separate_replicas_using_same_key_files_can_decrypt_each_others_values()
    {
        using var files = new TemporaryKeyDirectory();
        var key = new TestKey("primary", 1, files.WriteKey("primary.key"));
        using var firstReplica = TestEncryptionService.Create("primary", 1, key);
        using var secondReplica = TestEncryptionService.Create("primary", 1, key);

        var fromFirst = await EncryptAsync(firstReplica.Service, "from first");
        var fromSecond = await EncryptAsync(secondReplica.Service, "from second");

        await AssertDecryptsToAsync(secondReplica.Service, fromFirst, "from first");
        await AssertDecryptsToAsync(firstReplica.Service, fromSecond, "from second");
    }

    [Fact]
    public async Task Restored_key_file_and_cloned_envelope_recover_value_in_fresh_service()
    {
        using var sourceFiles = new TemporaryKeyDirectory();
        using var backupFiles = new TemporaryKeyDirectory();
        var sourcePath = sourceFiles.WriteKey("primary.key");
        using var sourceService = TestEncryptionService.Create(
            "primary", 1, new TestKey("primary", 1, sourcePath));
        var encrypted = await EncryptAsync(sourceService.Service, "backup restore secret");
        var envelopeBackup = EncryptionTestData.CloneEnvelope(encrypted);

        var restoredPath = backupFiles.GetPath("primary-restored.key");
        File.Copy(sourcePath, restoredPath);
        using var restoredService = TestEncryptionService.Create(
            "primary", 1, new TestKey("primary", 1, restoredPath));

        await AssertDecryptsToAsync(restoredService.Service, envelopeBackup, "backup restore secret");
    }

    private static async Task<EncryptedValue> EncryptAsync(EnvelopeEncryptionService service, string plaintext)
    {
        var result = await service.EncryptStringAsync(plaintext, EncryptionTestData.TenantContext());
        return Assert.IsType<EncryptedValue>(result.Value);
    }

    private static async Task AssertDecryptsToAsync(
        EnvelopeEncryptionService service,
        EncryptedValue encrypted,
        string expected)
    {
        var result = await service.DecryptStringAsync(encrypted, EncryptionTestData.TenantContext());
        var plaintext = Assert.IsType<string>(result.Value);
        Assert.Equal(expected, plaintext);
    }
}
