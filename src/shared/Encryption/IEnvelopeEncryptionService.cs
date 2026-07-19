using Pole.ResultType;

namespace Pole.Encryption;

public sealed record EnvelopeEncryptionKeyReference(
    string ProviderId,
    string KeyId,
    int KeyVersion,
    string KeyWrapAlgorithm,
    int KeyWrapAlgorithmVersion);

public interface IEnvelopeEncryptionService
{
    EnvelopeEncryptionKeyReference ActiveKey { get; }

    ValueTask<Result<EncryptedValue>> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<Result<byte[]>> DecryptAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EncryptedValue>> EncryptStringAsync(
        string plaintext,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<Result<string>> DecryptStringAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EncryptedValue>> RewrapAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);
}
