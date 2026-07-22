using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Security;

public sealed record DeploymentTargetCredentialRewrapSummary(
    long Examined,
    long Rewrapped,
    long ConcurrentChanges);

public interface IDeploymentTargetCredentialRewrapService
{
    Task<Result<DeploymentTargetCredentialRewrapSummary>> RewrapAllAsync(
        EnvelopeEncryptionKeyReference expectedActiveKey,
        CancellationToken cancellationToken = default);
}

public sealed class DeploymentTargetCredentialRewrapService(
    IDeploymentTargetDbContext dbContext,
    IEnvelopeEncryptionService envelopeEncryption) : IDeploymentTargetCredentialRewrapService
{
    private const int BatchSize = 100;

    public async Task<Result<DeploymentTargetCredentialRewrapSummary>> RewrapAllAsync(
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
                ? Builders<DeploymentTarget>.Filter.Empty
                : Builders<DeploymentTarget>.Filter.Gt(target => target.Id, cursor);
            var batch = await dbContext.DeploymentTargets
                .Find(filter)
                .SortBy(target => target.Id)
                .Limit(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                break;

            foreach (var target in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = target.Id;

                Result<EncryptedValue> rewrapResult;
                string encryptedField;
                EncryptedValue currentEnvelope;
                switch (target.Connection)
                {
                    case InClusterDeploymentTargetConnection:
                        continue;
                    case KubeconfigDeploymentTargetConnection kubeconfig:
                        examined++;
                        currentEnvelope = kubeconfig.EncryptedKubeconfig;
                        encryptedField = "Connection.EncryptedKubeconfig";
                        if (ReferencesKey(currentEnvelope, expectedActiveKey))
                            continue;
                        rewrapResult = await envelopeEncryption.RewrapAsync(
                            currentEnvelope,
                            DeploymentTargetConnectionEncryptionContext.ForKubeconfig(
                                target.Id,
                                kubeconfig.Version),
                            cancellationToken);
                        break;
                    case ServiceAccountDeploymentTargetConnection serviceAccount:
                        examined++;
                        currentEnvelope = serviceAccount.EncryptedCredential;
                        encryptedField = "Connection.EncryptedCredential";
                        if (ReferencesKey(currentEnvelope, expectedActiveKey))
                            continue;
                        rewrapResult = await envelopeEncryption.RewrapAsync(
                            currentEnvelope,
                            DeploymentTargetConnectionEncryptionContext.ForServiceAccount(
                                target.Id,
                                serviceAccount.Version),
                            cancellationToken);
                        break;
                    default:
                        return Failures.Internal(
                            "A deployment-target credential could not be rewrapped.",
                            "The stored connection kind is unsupported.");
                }

                if (!rewrapResult.TryGetValue(out var rewrappedEnvelope, out var failure))
                    return failure;
                if (!ReferencesKey(rewrappedEnvelope, expectedActiveKey))
                {
                    return Failures.Conflict(
                        "The active envelope-encryption key changed while deployment-target credentials were being rewrapped.");
                }

                var casFilter = Builders<DeploymentTarget>.Filter.And(
                    Builders<DeploymentTarget>.Filter.Eq(item => item.Id, target.Id),
                    Builders<DeploymentTarget>.Filter.Eq(
                        "Connection.Version",
                        target.Connection.Version),
                    Builders<DeploymentTarget>.Filter.Eq(
                        encryptedField,
                        currentEnvelope));
                var update = await dbContext.DeploymentTargets.UpdateOneAsync(
                    casFilter,
                    Builders<DeploymentTarget>.Update.Set(
                        encryptedField,
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

        return new DeploymentTargetCredentialRewrapSummary(
            examined,
            rewrapped,
            concurrentChanges);
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
