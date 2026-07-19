using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.ContainerRegistries.Domain;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public sealed record RegistryCredentialRewrapSummary(
    long Examined,
    long Rewrapped,
    long ConcurrentChanges);

public interface IRegistryCredentialRewrapService
{
    Task<Result<RegistryCredentialRewrapSummary>> RewrapAllAsync(
        EnvelopeEncryptionKeyReference expectedActiveKey,
        CancellationToken cancellationToken = default);
}

public sealed class RegistryCredentialRewrapService(
    IContainerRegistryDbContext dbContext,
    IEnvelopeEncryptionService envelopeEncryption) : IRegistryCredentialRewrapService
{
    private const int BatchSize = 100;

    public async Task<Result<RegistryCredentialRewrapSummary>> RewrapAllAsync(
        EnvelopeEncryptionKeyReference expectedActiveKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedActiveKey);
        if (expectedActiveKey != envelopeEncryption.ActiveKey)
        {
            return Failures.Conflict(
                "This Pole replica does not have the requested envelope-encryption key active.");
        }

        long examined = 0;
        long rewrapped = 0;
        long concurrentChanges = 0;
        string? cursor = null;
        while (true)
        {
            var filter = cursor is null
                ? Builders<RegistryCredential>.Filter.Empty
                : Builders<RegistryCredential>.Filter.Gt(credential => credential.Id, cursor);
            var batch = await dbContext.RegistryCredentials
                .Find(filter)
                .SortBy(credential => credential.Id)
                .Limit(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                break;

            foreach (var credential in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                cursor = credential.Id;
                if (credential.EncryptedSecret is null || credential.SecretVersion < 1)
                {
                    return Failures.Internal(
                        "A container registry credential could not be rewrapped.",
                        "The stored envelope identity is missing, invalid, or uses an unsupported legacy format.");
                }
                if (ReferencesKey(credential.EncryptedSecret, expectedActiveKey))
                    continue;

                var context = RegistryCredentialEncryptionContext.Create(
                    credential.TenantId,
                    credential.Id,
                    credential.SecretVersion);
                var result = await envelopeEncryption.RewrapAsync(
                    credential.EncryptedSecret,
                    context,
                    cancellationToken);
                if (!result.TryGetValue(out var rewrappedEnvelope, out var failure))
                    return failure;
                if (!ReferencesKey(rewrappedEnvelope, expectedActiveKey))
                {
                    return Failures.Conflict(
                        "The active envelope-encryption key changed while registry credentials were being rewrapped.");
                }

                var update = await dbContext.RegistryCredentials.UpdateOneAsync(
                    Builders<RegistryCredential>.Filter.And(
                        Builders<RegistryCredential>.Filter.Eq(item => item.Id, credential.Id),
                        Builders<RegistryCredential>.Filter.Eq(
                            item => item.EncryptedSecret,
                            credential.EncryptedSecret)),
                    Builders<RegistryCredential>.Update.Set(
                        item => item.EncryptedSecret,
                        rewrappedEnvelope),
                    cancellationToken: cancellationToken);
                if (update.ModifiedCount == 1)
                    rewrapped++;
                else
                    concurrentChanges++;
            }

            if (batch.Count < BatchSize)
                break;
        }

        return new RegistryCredentialRewrapSummary(examined, rewrapped, concurrentChanges);
    }

    private static bool ReferencesKey(
        EncryptedValue envelope,
        EnvelopeEncryptionKeyReference key) =>
        string.Equals(envelope.KeyProviderId, key.ProviderId, StringComparison.Ordinal) &&
        string.Equals(envelope.KeyEncryptionKeyId, key.KeyId, StringComparison.Ordinal) &&
        envelope.KeyEncryptionKeyVersion == key.KeyVersion &&
        string.Equals(envelope.KeyWrapAlgorithm, key.KeyWrapAlgorithm, StringComparison.Ordinal) &&
        envelope.KeyWrapAlgorithmVersion == key.KeyWrapAlgorithmVersion;
}
