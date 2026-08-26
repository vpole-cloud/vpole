using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Pole.ResultType;

namespace Pole.Encryption;

public sealed class EnvelopeEncryptionService : IEnvelopeEncryptionService
{
    public const int CurrentFormatVersion = 1;
    public const string ContentAlgorithm = "AES-256-GCM";
    public const int ContentAlgorithmVersion = 1;
    private const int DataEncryptionKeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int MaximumMetadataLength = 128;
    private const int MaximumWrappedKeyComponentSizeBytes = 64 * 1024;

    private readonly IReadOnlyDictionary<string, IKeyEncryptionProvider> _providers;
    private readonly IKeyEncryptionProvider _activeProvider;

    public EnvelopeEncryptionKeyReference ActiveKey
    {
        get
        {
            var activeKey = _activeProvider.ActiveKey;
            return new EnvelopeEncryptionKeyReference(
                _activeProvider.ProviderId,
                activeKey.Identity.Id,
                activeKey.Identity.Version,
                activeKey.Algorithm,
                activeKey.AlgorithmVersion);
        }
    }

    public EnvelopeEncryptionService(
        IEnumerable<IKeyEncryptionProvider> providers,
        IOptions<EnvelopeEncryptionOptions> options)
    {
        var providerDictionary = new Dictionary<string, IKeyEncryptionProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (!HasValidProviderMetadata(provider) || !providerDictionary.TryAdd(provider.ProviderId, provider))
                throw new InvalidOperationException("Key-encryption provider metadata must be valid and provider IDs must be unique.");
        }

        _providers = providerDictionary;
        if (!_providers.TryGetValue(options.Value.ActiveProviderId, out _activeProvider!))
            throw new InvalidOperationException("The active key-encryption provider is not registered.");
    }

    public async ValueTask<Result<EncryptedValue, EnvelopeEncryptionFailed>> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        var dataEncryptionKey = RandomNumberGenerator.GetBytes(DataEncryptionKeySizeBytes);
        try
        {
            var activeKey = _activeProvider.ActiveKey;
            var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            var ciphertext = new byte[plaintext.Length];
            var authenticationTag = new byte[TagSizeBytes];
            var payloadAssociatedData = AssociatedDataEncoder.ForPayload(
                CurrentFormatVersion,
                ContentAlgorithm,
                ContentAlgorithmVersion,
                context);
            try
            {
                using var aes = new AesGcm(dataEncryptionKey, TagSizeBytes);
                aes.Encrypt(nonce, plaintext.Span, ciphertext, authenticationTag, payloadAssociatedData);
            }
            catch (CryptographicException)
            {
                return new Err<EnvelopeEncryptionFailed>(EncryptFailure());
            }

            var wrappedKeyAssociatedData = AssociatedDataEncoder.ForWrappedKey(
                CurrentFormatVersion,
                _activeProvider.ProviderId,
                activeKey.Identity,
                activeKey.Algorithm,
                activeKey.AlgorithmVersion,
                ContentAlgorithm,
                ContentAlgorithmVersion,
                context);
            var wrappedResult = await _activeProvider.WrapKeyAsync(
                activeKey,
                dataEncryptionKey,
                wrappedKeyAssociatedData,
                cancellationToken);
            if (!wrappedResult.TryGetValue(out var wrappedKey, out _) || !HasValidWrappedKey(wrappedKey))
                return new Err<EnvelopeEncryptionFailed>(EncryptFailure());

            return new Ok<EncryptedValue>(new EncryptedValue(
                CurrentFormatVersion,
                _activeProvider.ProviderId,
                activeKey.Identity.Id,
                activeKey.Identity.Version,
                activeKey.Algorithm,
                activeKey.AlgorithmVersion,
                wrappedKey.Ciphertext,
                wrappedKey.Nonce,
                wrappedKey.AuthenticationTag,
                ContentAlgorithm,
                ContentAlgorithmVersion,
                ciphertext,
                nonce,
                authenticationTag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    public async ValueTask<Result<byte[], EnvelopeDecryptionFailed>> DecryptAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(encryptedValue);
        ArgumentNullException.ThrowIfNull(context);
        if (!HasValidMetadata(encryptedValue))
            return new Err<EnvelopeDecryptionFailed>(DecryptFailure());

        var keyResult = await UnwrapDataEncryptionKeyAsync(encryptedValue, context, cancellationToken);
        if (!keyResult.TryGetValue(out var dataEncryptionKey, out _))
            return new Err<EnvelopeDecryptionFailed>(DecryptFailure());

        try
        {
            return DecryptPayload(encryptedValue, context, dataEncryptionKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    public async ValueTask<Result<EncryptedValue, EnvelopeEncryptionFailed>> EncryptStringAsync(
        string plaintext,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return await EncryptAsync(bytes, context, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async ValueTask<Result<string, EnvelopeDecryptionFailed>> DecryptStringAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await DecryptAsync(encryptedValue, context, cancellationToken);
        if (!result.TryGetValue(out var plaintext, out var failure))
            return new Err<EnvelopeDecryptionFailed>(failure);
        try
        {
            return new Ok<string>(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async ValueTask<Result<EncryptedValue, EnvelopeRewrapFailed>> RewrapAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(encryptedValue);
        ArgumentNullException.ThrowIfNull(context);
        if (!HasValidMetadata(encryptedValue))
            return new Err<EnvelopeRewrapFailed>(RewrapFailure());

        var keyResult = await UnwrapDataEncryptionKeyAsync(encryptedValue, context, cancellationToken);
        if (!keyResult.TryGetValue(out var dataEncryptionKey, out _))
            return new Err<EnvelopeRewrapFailed>(RewrapFailure());

        try
        {
            var activeKey = _activeProvider.ActiveKey;
            if (string.Equals(encryptedValue.KeyProviderId, _activeProvider.ProviderId, StringComparison.Ordinal) &&
                string.Equals(encryptedValue.KeyEncryptionKeyId, activeKey.Identity.Id, StringComparison.Ordinal) &&
                encryptedValue.KeyEncryptionKeyVersion == activeKey.Identity.Version &&
                string.Equals(encryptedValue.KeyWrapAlgorithm, activeKey.Algorithm, StringComparison.Ordinal) &&
                encryptedValue.KeyWrapAlgorithmVersion == activeKey.AlgorithmVersion)
            {
                return new Ok<EncryptedValue>(encryptedValue);
            }

            var wrappedKeyAssociatedData = AssociatedDataEncoder.ForWrappedKey(
                encryptedValue.FormatVersion,
                _activeProvider.ProviderId,
                activeKey.Identity,
                activeKey.Algorithm,
                activeKey.AlgorithmVersion,
                encryptedValue.ContentEncryptionAlgorithm,
                encryptedValue.ContentEncryptionAlgorithmVersion,
                context);
            var wrappedResult = await _activeProvider.WrapKeyAsync(
                activeKey,
                dataEncryptionKey,
                wrappedKeyAssociatedData,
                cancellationToken);
            if (!wrappedResult.TryGetValue(out var wrappedKey, out _) || !HasValidWrappedKey(wrappedKey))
                return new Err<EnvelopeRewrapFailed>(RewrapFailure());

            return new Ok<EncryptedValue>(encryptedValue with
            {
                KeyProviderId = _activeProvider.ProviderId,
                KeyEncryptionKeyId = activeKey.Identity.Id,
                KeyEncryptionKeyVersion = activeKey.Identity.Version,
                KeyWrapAlgorithm = activeKey.Algorithm,
                KeyWrapAlgorithmVersion = activeKey.AlgorithmVersion,
                WrappedDataEncryptionKey = wrappedKey.Ciphertext,
                WrappedDataEncryptionKeyNonce = wrappedKey.Nonce,
                WrappedDataEncryptionKeyTag = wrappedKey.AuthenticationTag,
                Ciphertext = encryptedValue.Ciphertext.ToArray(),
                Nonce = encryptedValue.Nonce.ToArray(),
                AuthenticationTag = encryptedValue.AuthenticationTag.ToArray()
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    private async ValueTask<Result<byte[], EnvelopeDecryptionFailed>> UnwrapDataEncryptionKeyAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(encryptedValue.KeyProviderId, out var provider))
            return new Err<EnvelopeDecryptionFailed>(DecryptFailure());

        var keyIdentity = new KeyEncryptionKeyIdentity(
            encryptedValue.KeyEncryptionKeyId,
            encryptedValue.KeyEncryptionKeyVersion);
        var wrappedKeyAssociatedData = AssociatedDataEncoder.ForWrappedKey(
            encryptedValue.FormatVersion,
            encryptedValue.KeyProviderId,
            keyIdentity,
            encryptedValue.KeyWrapAlgorithm,
            encryptedValue.KeyWrapAlgorithmVersion,
            encryptedValue.ContentEncryptionAlgorithm,
            encryptedValue.ContentEncryptionAlgorithmVersion,
            context);
        var result = await provider.UnwrapKeyAsync(
            keyIdentity,
            encryptedValue.KeyWrapAlgorithm,
            encryptedValue.KeyWrapAlgorithmVersion,
            encryptedValue.WrappedDataEncryptionKey,
            encryptedValue.WrappedDataEncryptionKeyNonce,
            encryptedValue.WrappedDataEncryptionKeyTag,
            wrappedKeyAssociatedData,
            cancellationToken);
        if (!result.TryGetValue(out var dataEncryptionKey, out _) || dataEncryptionKey.Length != DataEncryptionKeySizeBytes)
        {
            if (dataEncryptionKey is not null)
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
            return new Err<EnvelopeDecryptionFailed>(DecryptFailure());
        }

        return new Ok<byte[]>(dataEncryptionKey);
    }

    private static Result<byte[], EnvelopeDecryptionFailed> DecryptPayload(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        byte[] dataEncryptionKey)
    {
        var plaintext = new byte[encryptedValue.Ciphertext.Length];
        var payloadAssociatedData = AssociatedDataEncoder.ForPayload(
            encryptedValue.FormatVersion,
            encryptedValue.ContentEncryptionAlgorithm,
            encryptedValue.ContentEncryptionAlgorithmVersion,
            context);
        try
        {
            using var aes = new AesGcm(dataEncryptionKey, TagSizeBytes);
            aes.Decrypt(
                encryptedValue.Nonce,
                encryptedValue.Ciphertext,
                encryptedValue.AuthenticationTag,
                plaintext,
                payloadAssociatedData);
            return new Ok<byte[]>(plaintext);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return new Err<EnvelopeDecryptionFailed>(DecryptFailure());
        }
    }

    private static bool HasValidMetadata(EncryptedValue value) =>
        value.FormatVersion == CurrentFormatVersion &&
        !string.IsNullOrWhiteSpace(value.KeyProviderId) &&
        value.KeyProviderId.Length <= MaximumMetadataLength &&
        !string.IsNullOrWhiteSpace(value.KeyEncryptionKeyId) &&
        value.KeyEncryptionKeyId.Length <= MaximumMetadataLength &&
        value.KeyEncryptionKeyVersion > 0 &&
        !string.IsNullOrWhiteSpace(value.KeyWrapAlgorithm) &&
        value.KeyWrapAlgorithm.Length <= MaximumMetadataLength &&
        value.KeyWrapAlgorithmVersion > 0 &&
        string.Equals(value.ContentEncryptionAlgorithm, ContentAlgorithm, StringComparison.Ordinal) &&
        value.ContentEncryptionAlgorithmVersion == ContentAlgorithmVersion &&
        HasValidWrappedKeyComponent(value.WrappedDataEncryptionKey) &&
        HasValidWrappedKeyComponent(value.WrappedDataEncryptionKeyNonce) &&
        HasValidWrappedKeyComponent(value.WrappedDataEncryptionKeyTag) &&
        value.Ciphertext is not null &&
        value.Nonce is { Length: NonceSizeBytes } &&
        value.AuthenticationTag is { Length: TagSizeBytes };

    private static bool HasValidProviderMetadata(IKeyEncryptionProvider? provider)
    {
        if (provider is null ||
            string.IsNullOrWhiteSpace(provider.ProviderId) ||
            provider.ProviderId.Length > MaximumMetadataLength)
        {
            return false;
        }

        var activeKey = provider.ActiveKey;
        return activeKey is not null &&
               activeKey.Identity is not null &&
               !string.IsNullOrWhiteSpace(activeKey.Identity.Id) &&
               activeKey.Identity.Id.Length <= MaximumMetadataLength &&
               activeKey.Identity.Version > 0 &&
               !string.IsNullOrWhiteSpace(activeKey.Algorithm) &&
               activeKey.Algorithm.Length <= MaximumMetadataLength &&
               activeKey.AlgorithmVersion > 0;
    }

    private static bool HasValidWrappedKey(WrappedDataEncryptionKey? wrappedKey) =>
        wrappedKey is not null &&
        HasValidWrappedKeyComponent(wrappedKey.Ciphertext) &&
        HasValidWrappedKeyComponent(wrappedKey.Nonce) &&
        HasValidWrappedKeyComponent(wrappedKey.AuthenticationTag);

    private static bool HasValidWrappedKeyComponent(byte[]? value) =>
        value is { Length: > 0 and <= MaximumWrappedKeyComponentSizeBytes };

    private static EnvelopeEncryptionFailed EncryptFailure() => new(
        "The protected value could not be encrypted.");

    private static EnvelopeDecryptionFailed DecryptFailure() => new(
        "The protected value could not be decrypted.");

    private static EnvelopeRewrapFailed RewrapFailure() => new(
        "The protected value data-encryption key could not be rewrapped.");
}
