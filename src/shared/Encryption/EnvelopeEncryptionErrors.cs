namespace Pole.Encryption;

public sealed record EnvelopeEncryptionFailed(string Message);

public sealed record EnvelopeDecryptionFailed(string Message);

public sealed record EnvelopeRewrapFailed(string Message);

public sealed record DataEncryptionKeyWrappingFailed(string Message);

public sealed record DataEncryptionKeyUnwrappingFailed(string Message);
