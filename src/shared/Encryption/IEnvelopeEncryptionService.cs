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

    ValueTask<Result<EncryptedValue, EnvelopeEncryptionFailed>> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<Result<byte[], EnvelopeDecryptionFailed>> DecryptAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EncryptedValue, EnvelopeEncryptionFailed>> EncryptStringAsync(
        string plaintext,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<Result<string, EnvelopeDecryptionFailed>> DecryptStringAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EncryptedValue, EnvelopeRewrapFailed>> RewrapAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);
}
