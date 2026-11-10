namespace Pole.Encryption;

public sealed record EnvelopeEncryptionKeyReference(
    string ProviderId,
    string KeyId,
    int KeyVersion,
    string KeyWrapAlgorithm,
    int KeyWrapAlgorithmVersion
);

public interface IEnvelopeEncryptionService
{
    EnvelopeEncryptionKeyReference ActiveKey { get; }

    ValueTask<EnvelopeEncryptionOutcome> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<EnvelopeDecryptionOutcome> DecryptAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<EnvelopeEncryptionOutcome> EncryptStringAsync(
        string plaintext,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<EnvelopeStringDecryptionOutcome> DecryptStringAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<EnvelopeRewrapOutcome> RewrapAsync(
        EncryptedValue encryptedValue,
        EnvelopeEncryptionContext context,
        CancellationToken cancellationToken = default);
}
