namespace Pole.Encryption;

public sealed record EnvelopeEncryptionFailed(
    string Message
);

public sealed record EnvelopeDecryptionFailed(
    string Message
);

public sealed record EnvelopeRewrapFailed(
    string Message
);

public sealed record DataEncryptionKeyWrappingFailed(
    string Message
);

public sealed record DataEncryptionKeyUnwrappingFailed(
    string Message
);

public union EnvelopeEncryptionOutcome(EncryptedValue, EnvelopeEncryptionFailed);

public union EnvelopeDecryptionOutcome(byte[], EnvelopeDecryptionFailed);

public union EnvelopeStringDecryptionOutcome(string, EnvelopeDecryptionFailed);

public union EnvelopeRewrapOutcome(EncryptedValue, EnvelopeRewrapFailed);

public union DataEncryptionKeyWrappingOutcome(WrappedDataEncryptionKey, DataEncryptionKeyWrappingFailed);

public union DataEncryptionKeyUnwrappingOutcome(byte[], DataEncryptionKeyUnwrappingFailed);
