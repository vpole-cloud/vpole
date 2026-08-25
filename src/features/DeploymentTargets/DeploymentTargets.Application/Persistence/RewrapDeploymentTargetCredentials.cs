using MongoDB.Driver;
using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

public sealed record DeploymentTargetCredentialRewrapSummary(
    long Examined,
    long Rewrapped,
    long ConcurrentChanges);

public delegate Task<Result<
    DeploymentTargetCredentialRewrapSummary,
    DeploymentTargetCredentialRewrapConflict>> RewrapDeploymentTargetCredentials(
    EnvelopeEncryptionKeyReference expectedActiveKey,
    CancellationToken cancellationToken = default);

internal sealed class MongoRewrapDeploymentTargetCredentials(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IEnvelopeEncryptionService envelopeEncryption)
{
    private const int BatchSize = 100;

    public async Task<Result<
        DeploymentTargetCredentialRewrapSummary,
        DeploymentTargetCredentialRewrapConflict>> InvokeAsync(
        EnvelopeEncryptionKeyReference expectedActiveKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedActiveKey);
        if (expectedActiveKey != envelopeEncryption.ActiveKey)
        {
            return new Err<DeploymentTargetCredentialRewrapConflict>(
                new DeploymentTargetCredentialRewrapConflict());
        }

        long examined = 0;
        long rewrapped = 0;
        long concurrentChanges = 0;
        string? cursor = null;
        while (true)
        {
            var filter = DeploymentTargetFilters.AfterId(cursor);
            var batch = await deploymentTargets
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
                FilterDefinition<DeploymentTarget> casFilter;
                switch (target.Connection)
                {
                    case InClusterDeploymentTargetConnection:
                        continue;
                    case KubeconfigDeploymentTargetConnection kubeconfig:
                        examined++;
                        currentEnvelope = kubeconfig.EncryptedKubeconfig;
                        encryptedField = "Connection.EncryptedKubeconfig";
                        casFilter = DeploymentTargetFilters.AtKubeconfigEnvelope(
                            target.Id,
                            kubeconfig.Version,
                            currentEnvelope);
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
                        casFilter = DeploymentTargetFilters.AtServiceAccountCredentialEnvelope(
                            target.Id,
                            serviceAccount.Version,
                            currentEnvelope);
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
                        throw new InvalidOperationException(
                            "A deployment-target credential has an unsupported stored connection kind.");
                }

                if (!rewrapResult.TryGetValue(out var rewrappedEnvelope, out _))
                {
                    throw new InvalidOperationException(
                        "A deployment-target credential could not be rewrapped by the active encryption provider.");
                }
                if (!ReferencesKey(rewrappedEnvelope, expectedActiveKey))
                {
                    return new Err<DeploymentTargetCredentialRewrapConflict>(
                        new DeploymentTargetCredentialRewrapConflict());
                }

                var update = await deploymentTargets.UpdateOneAsync(
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

        return new Ok<DeploymentTargetCredentialRewrapSummary>(
            new DeploymentTargetCredentialRewrapSummary(
                examined,
                rewrapped,
                concurrentChanges));
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
