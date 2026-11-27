// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Pole.Encryption;

public sealed class LocalKeyFileKeyEncryptionProvider : IKeyEncryptionProvider, IDisposable
{
    public const string StableProviderId = "local-key-file";
    public const string Algorithm = "AES-256-GCM";
    public const int AlgorithmVersion = 1;
    private const int DataEncryptionKeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private readonly object _keyAccessLock = new();

    private readonly Dictionary<KeyEncryptionKeyIdentity, byte[]> _keys;
    private bool _disposed;

    public LocalKeyFileKeyEncryptionProvider(IOptions<LocalKeyFileOptions> options)
    {
        var configuration = options.Value;
        ActiveKey = new KeyEncryptionKeyDescriptor(
            new KeyEncryptionKeyIdentity(configuration.ActiveKeyId, configuration.ActiveKeyVersion),
            Algorithm,
            AlgorithmVersion);
        _keys = new Dictionary<KeyEncryptionKeyIdentity, byte[]>();
        try
        {
            foreach (var configuredKey in configuration.Keys)
            {
                if (!LocalKeyFileReader.TryRead(configuredKey.Path, out var keyMaterial))
                    throw new InvalidOperationException(
                        "A configured LocalKeyFile key became unavailable after startup validation.");
                _keys.Add(new KeyEncryptionKeyIdentity(configuredKey.Id, configuredKey.Version), keyMaterial);
            }
        }
        catch
        {
            foreach (var key in _keys.Values)
                CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_keyAccessLock)
        {
            if (_disposed)
                return;
            foreach (var key in _keys.Values)
                CryptographicOperations.ZeroMemory(key);
            _disposed = true;
        }
    }

    public string ProviderId => StableProviderId;
    public KeyEncryptionKeyDescriptor ActiveKey { get; }

    public ValueTask<DataEncryptionKeyWrappingOutcome> WrapKeyAsync(
        KeyEncryptionKeyDescriptor keyDescriptor,
        ReadOnlyMemory<byte> dataEncryptionKey,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_keyAccessLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (keyDescriptor != ActiveKey || dataEncryptionKey.Length != DataEncryptionKeySizeBytes ||
                !_keys.TryGetValue(keyDescriptor.Identity, out var key))
                return ValueTask.FromResult<DataEncryptionKeyWrappingOutcome>(KeyWrapFailure());

            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
                var ciphertext = new byte[DataEncryptionKeySizeBytes];
                var tag = new byte[TagSizeBytes];
                using var aes = new AesGcm(key, TagSizeBytes);
                aes.Encrypt(nonce, dataEncryptionKey.Span, ciphertext, tag, associatedData.Span);
                return ValueTask.FromResult<DataEncryptionKeyWrappingOutcome>(
                    new WrappedDataEncryptionKey(ciphertext, nonce, tag));
            }
            catch (CryptographicException)
            {
                return ValueTask.FromResult<DataEncryptionKeyWrappingOutcome>(KeyWrapFailure());
            }
        }
    }

    public ValueTask<DataEncryptionKeyUnwrappingOutcome> UnwrapKeyAsync(
        KeyEncryptionKeyIdentity keyIdentity,
        string algorithm,
        int algorithmVersion,
        ReadOnlyMemory<byte> wrappedDataEncryptionKey,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> authenticationTag,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_keyAccessLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!string.Equals(algorithm, Algorithm, StringComparison.Ordinal) ||
                algorithmVersion != AlgorithmVersion || wrappedDataEncryptionKey.Length != DataEncryptionKeySizeBytes ||
                nonce.Length != NonceSizeBytes || authenticationTag.Length != TagSizeBytes ||
                !_keys.TryGetValue(keyIdentity, out var key))
                return ValueTask.FromResult<DataEncryptionKeyUnwrappingOutcome>(KeyUnwrapFailure());

            var dataEncryptionKey = new byte[DataEncryptionKeySizeBytes];
            try
            {
                using var aes = new AesGcm(key, TagSizeBytes);
                aes.Decrypt(
                    nonce.Span,
                    wrappedDataEncryptionKey.Span,
                    authenticationTag.Span,
                    dataEncryptionKey,
                    associatedData.Span);
                return ValueTask.FromResult<DataEncryptionKeyUnwrappingOutcome>(dataEncryptionKey);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
                return ValueTask.FromResult<DataEncryptionKeyUnwrappingOutcome>(KeyUnwrapFailure());
            }
        }
    }

    private static DataEncryptionKeyWrappingFailed KeyWrapFailure() => new(
        "The data-encryption key could not be wrapped.");

    private static DataEncryptionKeyUnwrappingFailed KeyUnwrapFailure() => new(
        "The data-encryption key could not be unwrapped.");
}
