using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Credentials;

namespace Pole.Features.ContainerRegistries.Application.Security;

public sealed record RegistryCredentialRewrapSummary(
    long Examined,
    long Rewrapped,
    long ConcurrentChanges
);

public sealed record RegistryCredentialRewrapRejected;

public sealed record RegistryCredentialRewrapFailed;

public union RegistryCredentialRewrapOutcome(
    RegistryCredentialRewrapSummary,
    RegistryCredentialRewrapRejected,
    RegistryCredentialRewrapFailed);

public delegate Task<RegistryCredentialRewrapOutcome> RewrapAllRegistryCredentials(
    EnvelopeEncryptionKeyReference expectedActiveKey,
    CancellationToken cancellationToken = default);

public sealed class RegistryCredentialRewrapService(
    RegistryCredentialRewrapPersistence persistence,
    IEnvelopeEncryptionService envelopeEncryption
)
{
    private const int BatchSize = 100;

    public async Task<RegistryCredentialRewrapOutcome> InvokeAsync(
        EnvelopeEncryptionKeyReference expectedActiveKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedActiveKey);
        if (expectedActiveKey != envelopeEncryption.ActiveKey)
            return new RegistryCredentialRewrapRejected();

        long examined = 0;
        long rewrapped = 0;
        long concurrentChanges = 0;
        string? cursor = null;
        while (true)
        {
            var batch = await persistence.LoadBatchAsync(cursor, BatchSize, cancellationToken);
            if (batch.Count == 0)
                break;

            foreach (var credential in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                cursor = credential.Id;
                if (credential.SecretVersion < 1 || ReferencesKey(credential.EncryptedSecret, expectedActiveKey))
                {
                    if (credential.SecretVersion < 1)
                        return new RegistryCredentialRewrapFailed();
                    continue;
                }

                var rewrappedEnvelope = await envelopeEncryption.RewrapAsync(
                    credential.EncryptedSecret,
                    RegistryCredentialEncryptionContext.Create(
                        credential.TenantId,
                        credential.Id,
                        credential.SecretVersion),
                    cancellationToken);
                if (rewrappedEnvelope is not EncryptedValue envelope || !ReferencesKey(envelope, expectedActiveKey))
                    return new RegistryCredentialRewrapFailed();

                if (await persistence.TryReplaceEnvelopeAsync(credential, envelope, cancellationToken))
                    rewrapped++;
                else
                    concurrentChanges++;
            }

            if (batch.Count < BatchSize)
                break;
        }

        return new RegistryCredentialRewrapSummary(examined, rewrapped, concurrentChanges);
    }

    private static bool ReferencesKey(EncryptedValue envelope, EnvelopeEncryptionKeyReference key) =>
        string.Equals(envelope.KeyProviderId, key.ProviderId, StringComparison.Ordinal) &&
        string.Equals(envelope.KeyEncryptionKeyId, key.KeyId, StringComparison.Ordinal) &&
        envelope.KeyEncryptionKeyVersion == key.KeyVersion &&
        string.Equals(envelope.KeyWrapAlgorithm, key.KeyWrapAlgorithm, StringComparison.Ordinal) &&
        envelope.KeyWrapAlgorithmVersion == key.KeyWrapAlgorithmVersion;
}
