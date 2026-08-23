using Pole.Encryption;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Connections;

internal sealed class DeploymentTargetConnectionProtector(
    IEnvelopeEncryptionService encryption)
{
    private const string KubeconfigValueName = "kubeconfig";
    private const string ServiceAccountCredentialValueName = "service-account-credential";

    public async ValueTask<Result<EncryptedValue, DeploymentTargetConnectionRejected>> ProtectKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        ReadOnlyMemory<byte> canonicalKubeconfig,
        CancellationToken cancellationToken = default)
    {
        if (canonicalKubeconfig.IsEmpty || canonicalKubeconfig.Length > ConnectionPayloadLimits.MaximumKubeconfigBytes)
        {
            return new Err<DeploymentTargetConnectionRejected>(
                ConnectionRejections.Validation(
                    "kubeconfig",
                    "The kubeconfig payload has an invalid size."));
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
                Success<EncryptedValue> success => new Ok<EncryptedValue>(success.Value),
                Failure<EncryptedValue> failure => throw new InvalidOperationException(
                    $"The deployment-target connection-protection service rejected encryption: {failure.Error.Message}"),
                _ => throw new InvalidOperationException(
                    "The connection-protection service returned an unsupported result case.")
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

    public async ValueTask<Result<byte[], StoredDeploymentTargetConnectionUnavailable>> UnprotectKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        EncryptedValue encryptedKubeconfig,
        CancellationToken cancellationToken = default)
    {
        if (!HasBoundedCiphertext(encryptedKubeconfig, ConnectionPayloadLimits.MaximumKubeconfigBytes))
        {
            return new Err<StoredDeploymentTargetConnectionUnavailable>(new());
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
                Success<byte[]> success => new Ok<byte[]>(success.Value),
                Failure<byte[]> => new Err<StoredDeploymentTargetConnectionUnavailable>(new()),
                _ => throw new InvalidOperationException(
                    "The connection-protection service returned an unsupported result case.")
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

    public async ValueTask<Result<EncryptedValue, DeploymentTargetConnectionRejected>> ProtectServiceAccountCredentialAsync(
        string deploymentTargetId,
        long connectionVersion,
        ReadOnlyMemory<byte> canonicalServiceAccountCredential,
        CancellationToken cancellationToken = default)
    {
        if (canonicalServiceAccountCredential.IsEmpty ||
            canonicalServiceAccountCredential.Length > ConnectionPayloadLimits.MaximumServiceAccountCredentialBytes)
        {
            return new Err<DeploymentTargetConnectionRejected>(ConnectionRejections.Validation(
                "serviceAccount",
                "The service-account credential payload has an invalid size."));
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
                Success<EncryptedValue> success => new Ok<EncryptedValue>(success.Value),
                Failure<EncryptedValue> failure => throw new InvalidOperationException(
                    $"The deployment-target connection-protection service rejected encryption: {failure.Error.Message}"),
                _ => throw new InvalidOperationException(
                    "The connection-protection service returned an unsupported result case.")
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

    public async ValueTask<Result<byte[], StoredDeploymentTargetConnectionUnavailable>> UnprotectServiceAccountCredentialAsync(
        string deploymentTargetId,
        long connectionVersion,
        EncryptedValue encryptedCredential,
        CancellationToken cancellationToken = default)
    {
        if (!HasBoundedCiphertext(
                encryptedCredential,
                ConnectionPayloadLimits.MaximumServiceAccountCredentialBytes))
        {
            return new Err<StoredDeploymentTargetConnectionUnavailable>(new());
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
                Success<byte[]> success => new Ok<byte[]>(success.Value),
                Failure<byte[]> => new Err<StoredDeploymentTargetConnectionUnavailable>(new()),
                _ => throw new InvalidOperationException(
                    "The connection-protection service returned an unsupported result case.")
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

    private static bool HasBoundedCiphertext(EncryptedValue? encryptedValue, int maximumBytes) =>
        encryptedValue?.Ciphertext is { Length: > 0 } ciphertext &&
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
        long connectionVersion) =>
        DeploymentTargetConnectionEncryptionContext.Create(
            deploymentTargetId,
            connectionVersion,
            KubeconfigValueName);

    public static EnvelopeEncryptionContext ForServiceAccount(
        string deploymentTargetId,
        long connectionVersion) =>
        DeploymentTargetConnectionEncryptionContext.Create(
            deploymentTargetId,
            connectionVersion,
            ServiceAccountValueName);
}
