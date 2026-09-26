using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Connections;

internal union ProtectDeploymentTargetCredentialOutcome(
    EncryptedValue,
    DeploymentTargetConnectionRejected);

internal union UnprotectDeploymentTargetCredentialOutcome(
    byte[],
    StoredDeploymentTargetConnectionUnavailable);

internal sealed class DeploymentTargetConnectionProtector(
    IEnvelopeEncryptionService encryption)
{
    private const string KubeconfigValueName = "kubeconfig";
    private const string ServiceAccountCredentialValueName = "service-account-credential";

    public async ValueTask<ProtectDeploymentTargetCredentialOutcome> ProtectKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        ReadOnlyMemory<byte> canonicalKubeconfig,
        CancellationToken cancellationToken = default)
    {
        if (canonicalKubeconfig.IsEmpty || canonicalKubeconfig.Length > ConnectionPayloadLimits.MaximumKubeconfigBytes)
        {
            return ConnectionRejections.Validation(
                    "kubeconfig",
                    "The kubeconfig payload has an invalid size.");
        }

        var context = DeploymentTargetConnectionEncryptionContext.Create(
            deploymentTargetId,
            connectionVersion,
            KubeconfigValueName);

        try
        {
            var encrypted = await encryption.EncryptAsync(canonicalKubeconfig, context, cancellationToken);
            return encrypted switch
            {
                Ok<EncryptedValue> success => success.Value,
                Err<EnvelopeEncryptionFailed> failure => throw new InvalidOperationException(
                    $"The deployment-target connection-protection service rejected encryption: {failure.Value.Message}")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The deployment-target connection could not be protected.",
                exception);
        }
    }

    public async ValueTask<UnprotectDeploymentTargetCredentialOutcome> UnprotectKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        EncryptedValue encryptedKubeconfig,
        CancellationToken cancellationToken = default)
    {
        if (!HasBoundedCiphertext(encryptedKubeconfig, ConnectionPayloadLimits.MaximumKubeconfigBytes))
        {
            return new StoredDeploymentTargetConnectionUnavailable();
        }

        var context = DeploymentTargetConnectionEncryptionContext.Create(
            deploymentTargetId,
            connectionVersion,
            KubeconfigValueName);

        try
        {
            var decrypted = await encryption.DecryptAsync(encryptedKubeconfig, context, cancellationToken);
            return decrypted switch
            {
                Ok<byte[]> success => success.Value,
                Err<EnvelopeDecryptionFailed> => new StoredDeploymentTargetConnectionUnavailable()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The deployment-target connection could not be decrypted.",
                exception);
        }
    }

    public async ValueTask<ProtectDeploymentTargetCredentialOutcome> ProtectServiceAccountCredentialAsync(
        string deploymentTargetId,
        long connectionVersion,
        ReadOnlyMemory<byte> canonicalServiceAccountCredential,
        CancellationToken cancellationToken = default)
    {
        if (canonicalServiceAccountCredential.IsEmpty ||
            canonicalServiceAccountCredential.Length > ConnectionPayloadLimits.MaximumServiceAccountCredentialBytes)
        {
            return ConnectionRejections.Validation(
                "serviceAccount",
                "The service-account credential payload has an invalid size.");
        }

        var context = DeploymentTargetConnectionEncryptionContext.Create(
            deploymentTargetId,
            connectionVersion,
            ServiceAccountCredentialValueName);

        try
        {
            var encrypted = await encryption.EncryptAsync(
                canonicalServiceAccountCredential,
                context,
                cancellationToken);
            return encrypted switch
            {
                Ok<EncryptedValue> success => success.Value,
                Err<EnvelopeEncryptionFailed> failure => throw new InvalidOperationException(
                    $"The deployment-target connection-protection service rejected encryption: {failure.Value.Message}")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The deployment-target connection could not be protected.",
                exception);
        }
    }

    public async ValueTask<UnprotectDeploymentTargetCredentialOutcome> UnprotectServiceAccountCredentialAsync(
        string deploymentTargetId,
        long connectionVersion,
        EncryptedValue encryptedCredential,
        CancellationToken cancellationToken = default)
    {
        if (!HasBoundedCiphertext(
                encryptedCredential,
                ConnectionPayloadLimits.MaximumServiceAccountCredentialBytes))
        {
            return new StoredDeploymentTargetConnectionUnavailable();
        }

        var context = DeploymentTargetConnectionEncryptionContext.Create(
            deploymentTargetId,
            connectionVersion,
            ServiceAccountCredentialValueName);

        try
        {
            var decrypted = await encryption.DecryptAsync(encryptedCredential, context, cancellationToken);
            return decrypted switch
            {
                Ok<byte[]> success => success.Value,
                Err<EnvelopeDecryptionFailed> => new StoredDeploymentTargetConnectionUnavailable()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "The deployment-target connection could not be decrypted.",
                exception);
        }
    }

    private static bool HasBoundedCiphertext(EncryptedValue? encryptedValue, int maximumBytes) => encryptedValue?.Ciphertext is { Length: > 0 } ciphertext &&
        ciphertext.Length <= maximumBytes;
}

internal static class DeploymentTargetConnectionEncryptionContext
{
    private const string PlatformScopeId = "pole-control-plane";
    private const string ObjectType = "deployment-target-connection";
    private const string KubeconfigValueName = "kubeconfig";
    private const string ServiceAccountValueName = "service-account-credential";

    public static EnvelopeEncryptionContext Create(
        string deploymentTargetId,
        long connectionVersion,
        string valueName)
    {
        if (string.IsNullOrWhiteSpace(deploymentTargetId) ||
            deploymentTargetId.Length > ConnectionPayloadLimits.MaximumDeploymentTargetIdCharacters ||
            connectionVersion < 1)
        {
            throw new ArgumentException(
                "The deployment-target encryption context is invalid.",
                nameof(deploymentTargetId));
        }

        try
        {
            return EnvelopeEncryptionContext.ForPlatform(
                PlatformScopeId,
                ObjectType,
                deploymentTargetId,
                valueName,
                connectionVersion);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "The deployment-target encryption context is invalid.",
                nameof(deploymentTargetId),
                exception);
        }
    }

    public static EnvelopeEncryptionContext ForKubeconfig(
        string deploymentTargetId,
        long connectionVersion) => DeploymentTargetConnectionEncryptionContext.Create(
            deploymentTargetId,
            connectionVersion,
            KubeconfigValueName);

    public static EnvelopeEncryptionContext ForServiceAccount(
        string deploymentTargetId,
        long connectionVersion) => DeploymentTargetConnectionEncryptionContext.Create(
            deploymentTargetId,
            connectionVersion,
            ServiceAccountValueName);
}
