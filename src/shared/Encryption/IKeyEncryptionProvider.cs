namespace Pole.Encryption;

public sealed record KeyEncryptionKeyIdentity(
    string Id,
    int Version);

public sealed record KeyEncryptionKeyDescriptor(
    KeyEncryptionKeyIdentity Identity,
    string Algorithm,
    int AlgorithmVersion);

public sealed record WrappedDataEncryptionKey(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] AuthenticationTag)
{
    public override string ToString() => "WrappedDataEncryptionKey([REDACTED])";
}

public interface IKeyEncryptionProvider
{
    string ProviderId { get; }
    KeyEncryptionKeyDescriptor ActiveKey { get; }

    ValueTask<DataEncryptionKeyWrappingOutcome> WrapKeyAsync(
        KeyEncryptionKeyDescriptor key,
        ReadOnlyMemory<byte> dataEncryptionKey,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default);

    ValueTask<DataEncryptionKeyUnwrappingOutcome> UnwrapKeyAsync(
        KeyEncryptionKeyIdentity key,
        string algorithm,
        int algorithmVersion,
        ReadOnlyMemory<byte> wrappedDataEncryptionKey,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> authenticationTag,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default);
}
