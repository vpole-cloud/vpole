using Pole.ResultType;

namespace Pole.Encryption.Tests;

public sealed class EnvelopeEncryptionServiceTests
{
    [Fact]
    public async Task Bytes_round_trip()
    {
        using var files = new TemporaryKeyDirectory();
        using var encryption = CreateService(files);
        var plaintext = new byte[] { 0, 1, 2, 3, 127, 128, 254, 255 };

        var encrypted = await EncryptAsync(encryption.Service, plaintext, EncryptionTestData.TenantContext());
        var decrypted = await DecryptAsync(encryption.Service, encrypted, EncryptionTestData.TenantContext());

        Assert.True(plaintext.SequenceEqual(decrypted));
    }

    [Fact]
    public async Task Empty_value_round_trips()
    {
        using var files = new TemporaryKeyDirectory();
        using var encryption = CreateService(files);

        var encrypted = await EncryptAsync(encryption.Service, [], EncryptionTestData.TenantContext());
        var decrypted = await DecryptAsync(encryption.Service, encrypted, EncryptionTestData.TenantContext());

        Assert.Empty(encrypted.Ciphertext);
        Assert.Empty(decrypted);
    }

    [Fact]
    public async Task Unicode_string_round_trips()
    {
        using var files = new TemporaryKeyDirectory();
        using var encryption = CreateService(files);
        const string plaintext = "пароль-🔐-秘密";

        var encryptedResult = await encryption.Service.EncryptStringAsync(plaintext, EncryptionTestData.TenantContext());
        Assert.True(encryptedResult.TryGetValue(out var encrypted, out var encryptionFailure), encryptionFailure?.Message);
        var decryptedResult = await encryption.Service.DecryptStringAsync(encrypted, EncryptionTestData.TenantContext());

        Assert.True(decryptedResult.TryGetValue(out var decrypted, out var decryptionFailure), decryptionFailure?.Message);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task Each_encryption_uses_a_fresh_data_key_and_payload_nonce()
    {
        var provider = new CapturingKeyEncryptionProvider();
        var service = new EnvelopeEncryptionService(
            [provider],
            Microsoft.Extensions.Options.Options.Create(new EnvelopeEncryptionOptions
            {
                ActiveProviderId = provider.ProviderId
            }));

        var first = await EncryptAsync(service, [1, 2, 3], EncryptionTestData.TenantContext());
        var second = await EncryptAsync(service, [1, 2, 3], EncryptionTestData.TenantContext());

        Assert.Equal(2, provider.CapturedKeys.Count);
        Assert.False(provider.CapturedKeys[0].SequenceEqual(provider.CapturedKeys[1]));
        Assert.False(first.Nonce.SequenceEqual(second.Nonce));
    }

    [Theory]
    [InlineData("WrappedDataEncryptionKey")]
    [InlineData("WrappedDataEncryptionKeyNonce")]
    [InlineData("WrappedDataEncryptionKeyTag")]
    [InlineData("Ciphertext")]
    [InlineData("Nonce")]
    [InlineData("AuthenticationTag")]
    public async Task Tampering_any_encrypted_byte_field_fails_closed(string field)
    {
        using var files = new TemporaryKeyDirectory();
        using var encryption = CreateService(files);
        var original = await EncryptAsync(encryption.Service, [10, 20, 30], EncryptionTestData.TenantContext());
        var clone = EncryptionTestData.CloneEnvelope(original);
        var tampered = field switch
        {
            "WrappedDataEncryptionKey" => clone with
            {
                WrappedDataEncryptionKey = EncryptionTestData.FlipFirstByte(clone.WrappedDataEncryptionKey)
            },
            "WrappedDataEncryptionKeyNonce" => clone with
            {
                WrappedDataEncryptionKeyNonce = EncryptionTestData.FlipFirstByte(clone.WrappedDataEncryptionKeyNonce)
            },
            "WrappedDataEncryptionKeyTag" => clone with
            {
                WrappedDataEncryptionKeyTag = EncryptionTestData.FlipFirstByte(clone.WrappedDataEncryptionKeyTag)
            },
            "Ciphertext" => clone with { Ciphertext = EncryptionTestData.FlipFirstByte(clone.Ciphertext) },
            "Nonce" => clone with { Nonce = EncryptionTestData.FlipFirstByte(clone.Nonce) },
            "AuthenticationTag" => clone with
            {
                AuthenticationTag = EncryptionTestData.FlipFirstByte(clone.AuthenticationTag)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        await AssertDecryptFailsAsync(encryption.Service, tampered, EncryptionTestData.TenantContext());
    }

    [Theory]
    [InlineData("FormatVersion")]
    [InlineData("KeyProviderId")]
    [InlineData("KeyEncryptionKeyId")]
    [InlineData("KeyEncryptionKeyVersion")]
    [InlineData("KeyWrapAlgorithm")]
    [InlineData("KeyWrapAlgorithmVersion")]
    [InlineData("ContentEncryptionAlgorithm")]
    [InlineData("ContentEncryptionAlgorithmVersion")]
    public async Task Tampering_authenticated_metadata_fails_closed(string field)
    {
        using var files = new TemporaryKeyDirectory();
        using var encryption = CreateService(files);
        var original = await EncryptAsync(encryption.Service, [10, 20, 30], EncryptionTestData.TenantContext());
        var clone = EncryptionTestData.CloneEnvelope(original);
        var tampered = field switch
        {
            "FormatVersion" => clone with { FormatVersion = clone.FormatVersion + 1 },
            "KeyProviderId" => clone with { KeyProviderId = clone.KeyProviderId + "-other" },
            "KeyEncryptionKeyId" => clone with { KeyEncryptionKeyId = clone.KeyEncryptionKeyId + "-other" },
            "KeyEncryptionKeyVersion" => clone with
            {
                KeyEncryptionKeyVersion = clone.KeyEncryptionKeyVersion + 1
            },
            "KeyWrapAlgorithm" => clone with { KeyWrapAlgorithm = clone.KeyWrapAlgorithm + "-other" },
            "KeyWrapAlgorithmVersion" => clone with
            {
                KeyWrapAlgorithmVersion = clone.KeyWrapAlgorithmVersion + 1
            },
            "ContentEncryptionAlgorithm" => clone with
            {
                ContentEncryptionAlgorithm = clone.ContentEncryptionAlgorithm + "-other"
            },
            "ContentEncryptionAlgorithmVersion" => clone with
            {
                ContentEncryptionAlgorithmVersion = clone.ContentEncryptionAlgorithmVersion + 1
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        await AssertDecryptFailsAsync(encryption.Service, tampered, EncryptionTestData.TenantContext());
    }

    [Theory]
    [InlineData("Tenant")]
    [InlineData("ObjectType")]
    [InlineData("ObjectId")]
    [InlineData("ValueName")]
    [InlineData("ValueVersion")]
    [InlineData("PlatformScope")]
    public async Task Substituting_authenticated_context_fails_closed(string substitution)
    {
        using var files = new TemporaryKeyDirectory();
        using var encryption = CreateService(files);
        var encrypted = await EncryptAsync(encryption.Service, [10, 20, 30], EncryptionTestData.TenantContext());
        var substituted = substitution switch
        {
            "Tenant" => EnvelopeEncryptionContext.ForTenant(
                "tenant-b", "git-provider", "provider-42", "access-token", 7),
            "ObjectType" => EnvelopeEncryptionContext.ForTenant(
                "tenant-a", "container-registry", "provider-42", "access-token", 7),
            "ObjectId" => EnvelopeEncryptionContext.ForTenant(
                "tenant-a", "git-provider", "provider-43", "access-token", 7),
            "ValueName" => EnvelopeEncryptionContext.ForTenant(
                "tenant-a", "git-provider", "provider-42", "refresh-token", 7),
            "ValueVersion" => EnvelopeEncryptionContext.ForTenant(
                "tenant-a", "git-provider", "provider-42", "access-token", 8),
            "PlatformScope" => EnvelopeEncryptionContext.ForPlatform(
                "tenant-a", "git-provider", "provider-42", "access-token", 7),
            _ => throw new ArgumentOutOfRangeException(nameof(substitution))
        };

        await AssertDecryptFailsAsync(encryption.Service, encrypted, substituted);
    }

    [Fact]
    public async Task Same_key_identity_with_different_material_cannot_decrypt()
    {
        using var firstFiles = new TemporaryKeyDirectory();
        using var secondFiles = new TemporaryKeyDirectory();
        using var first = TestEncryptionService.Create(
            "primary", 1, new TestKey("primary", 1, firstFiles.WriteKey("primary.key")));
        using var second = TestEncryptionService.Create(
            "primary", 1, new TestKey("primary", 1, secondFiles.WriteKey("primary.key")));
        var encrypted = await EncryptAsync(first.Service, [1, 2, 3], EncryptionTestData.TenantContext());

        await AssertDecryptFailsAsync(second.Service, encrypted, EncryptionTestData.TenantContext());
    }

    [Fact]
    public async Task Missing_envelope_key_cannot_decrypt()
    {
        using var files = new TemporaryKeyDirectory();
        var oldPath = files.WriteKey("old.key");
        var otherPath = files.WriteKey("other.key");
        using var oldService = TestEncryptionService.Create("old", 1, new TestKey("old", 1, oldPath));
        using var serviceWithoutOldKey = TestEncryptionService.Create(
            "other", 1, new TestKey("other", 1, otherPath));
        var encrypted = await EncryptAsync(oldService.Service, [1, 2, 3], EncryptionTestData.TenantContext());

        await AssertDecryptFailsAsync(serviceWithoutOldKey.Service, encrypted, EncryptionTestData.TenantContext());
    }

    [Fact]
    public async Task Concurrent_use_of_one_service_round_trips_and_keeps_nonces_unique()
    {
        using var files = new TemporaryKeyDirectory();
        using var encryption = CreateService(files);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operations = Enumerable.Range(0, 64).Select(index => Task.Run(async () =>
            {
                await start.Task;
                var plaintext = BitConverter.GetBytes(index);
                var context = EnvelopeEncryptionContext.ForTenant(
                    "tenant-a", "concurrency-test", index.ToString(), "value", 1);
                var encrypted = await EncryptAsync(encryption.Service, plaintext, context);
                var decrypted = await DecryptAsync(encryption.Service, encrypted, context);
                Assert.True(plaintext.SequenceEqual(decrypted));
                return Convert.ToBase64String(encrypted.Nonce);
            }))
            .ToArray();

        start.SetResult();

        var nonces = await Task.WhenAll(operations);

        Assert.Equal(nonces.Length, nonces.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Invalid_provider_metadata_is_rejected_before_use()
    {
        var provider = new StubKeyEncryptionProvider(keyWrapAlgorithm: string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(() => new EnvelopeEncryptionService(
            [provider],
            Microsoft.Extensions.Options.Options.Create(new EnvelopeEncryptionOptions
            {
                ActiveProviderId = provider.ProviderId
            })));

        Assert.DoesNotContain(provider.ActiveKey.Identity.Id, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_wrapped_key_from_provider_fails_closed()
    {
        var provider = new StubKeyEncryptionProvider(
            wrappedKey: new WrappedDataEncryptionKey([], new byte[12], new byte[16]));
        var service = new EnvelopeEncryptionService(
            [provider],
            Microsoft.Extensions.Options.Options.Create(new EnvelopeEncryptionOptions
            {
                ActiveProviderId = provider.ProviderId
            }));

        var result = await service.EncryptAsync(new byte[] { 1, 2, 3 }, EncryptionTestData.TenantContext());

        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<InternalFailure>(failure);
    }

    private static TestEncryptionService CreateService(TemporaryKeyDirectory files) =>
        TestEncryptionService.Create(
            "primary", 1, new TestKey("primary", 1, files.WriteKey("primary.key")));

    private static async Task<EncryptedValue> EncryptAsync(
        EnvelopeEncryptionService service,
        byte[] plaintext,
        EnvelopeEncryptionContext context)
    {
        var result = await service.EncryptAsync(plaintext, context);
        Assert.True(result.TryGetValue(out var encrypted, out var failure), failure?.Message);
        return encrypted;
    }

    private static async Task<byte[]> DecryptAsync(
        EnvelopeEncryptionService service,
        EncryptedValue value,
        EnvelopeEncryptionContext context)
    {
        var result = await service.DecryptAsync(value, context);
        Assert.True(result.TryGetValue(out var plaintext, out var failure), failure?.Message);
        return plaintext;
    }

    private static async Task AssertDecryptFailsAsync(
        EnvelopeEncryptionService service,
        EncryptedValue value,
        EnvelopeEncryptionContext context)
    {
        var result = await service.DecryptAsync(value, context);
        Assert.False(result.TryGetValue(out _, out var failure));
        Assert.IsType<InternalFailure>(failure);
    }

    private sealed class CapturingKeyEncryptionProvider : IKeyEncryptionProvider
    {
        public string ProviderId => "capturing-provider";
        public KeyEncryptionKeyDescriptor ActiveKey { get; } = new(
            new KeyEncryptionKeyIdentity("capturing-key", 1),
            "test-only",
            1);
        public List<byte[]> CapturedKeys { get; } = [];

        public ValueTask<Result<WrappedDataEncryptionKey>> WrapKeyAsync(
            KeyEncryptionKeyDescriptor key,
            ReadOnlyMemory<byte> dataEncryptionKey,
            ReadOnlyMemory<byte> associatedData,
            CancellationToken cancellationToken = default)
        {
            CapturedKeys.Add(dataEncryptionKey.ToArray());
            return ValueTask.FromResult<Result<WrappedDataEncryptionKey>>(
                new WrappedDataEncryptionKey(new byte[32], new byte[12], new byte[16]));
        }

        public ValueTask<Result<byte[]>> UnwrapKeyAsync(
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

    private sealed class StubKeyEncryptionProvider(
        string keyWrapAlgorithm = "stub-wrap",
        WrappedDataEncryptionKey? wrappedKey = null) : IKeyEncryptionProvider
    {
        public string ProviderId => "stub-provider";
        public KeyEncryptionKeyDescriptor ActiveKey { get; } = new(
            new KeyEncryptionKeyIdentity("sensitive-stub-key-id", 1),
            keyWrapAlgorithm,
            1);

        public ValueTask<Result<WrappedDataEncryptionKey>> WrapKeyAsync(
            KeyEncryptionKeyDescriptor key,
            ReadOnlyMemory<byte> dataEncryptionKey,
            ReadOnlyMemory<byte> associatedData,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Result<WrappedDataEncryptionKey>>(
                wrappedKey ?? new WrappedDataEncryptionKey(new byte[32], new byte[12], new byte[16]));

        public ValueTask<Result<byte[]>> UnwrapKeyAsync(
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
