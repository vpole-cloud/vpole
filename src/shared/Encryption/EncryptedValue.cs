namespace Pole.Encryption;

public sealed record EncryptedValue(
    int FormatVersion,
    string KeyProviderId,
    string KeyEncryptionKeyId,
    int KeyEncryptionKeyVersion,
    string KeyWrapAlgorithm,
    int KeyWrapAlgorithmVersion,
    byte[] WrappedDataEncryptionKey,
    byte[] WrappedDataEncryptionKeyNonce,
    byte[] WrappedDataEncryptionKeyTag,
    string ContentEncryptionAlgorithm,
    int ContentEncryptionAlgorithmVersion,
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] AuthenticationTag)
{
    public override string ToString() =>
        $"EncryptedValue(format={FormatVersion}, provider={KeyProviderId}, contentAlgorithm={ContentEncryptionAlgorithm})";
}
