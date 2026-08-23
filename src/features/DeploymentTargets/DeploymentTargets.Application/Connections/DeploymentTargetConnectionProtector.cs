using Pole.Encryption;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Connections;

internal sealed class DeploymentTargetConnectionProtector(
    IEnvelopeEncryptionService encryption)
{
    private const string KubeconfigValueName = "kubeconfig";
    private const string ServiceAccountCredentialValueName = "service-account-credential";

    public async ValueTask<Result<EncryptedValue>> ProtectKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        ReadOnlyMemory<byte> canonicalKubeconfig,
        CancellationToken cancellationToken = default)
    {
        if (canonicalKubeconfig.IsEmpty || canonicalKubeconfig.Length > ConnectionPayloadLimits.MaximumKubeconfigBytes)
        {
            return ConnectionFailures.Validation("kubeconfig", "The kubeconfig payload has an invalid size.");
        }

        var contextResult = DeploymentTargetConnectionEncryptionContext.TryCreate(
            deploymentTargetId,
            connectionVersion,
            KubeconfigValueName);
        if (!contextResult.TryGetValue(out var context, out var failure))
            return failure;

        try
        {
            return await encryption.EncryptAsync(canonicalKubeconfig, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }
    }

    public async ValueTask<Result<byte[]>> UnprotectKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        EncryptedValue encryptedKubeconfig,
        CancellationToken cancellationToken = default)
    {
        if (!HasBoundedCiphertext(encryptedKubeconfig, ConnectionPayloadLimits.MaximumKubeconfigBytes))
        {
            return ConnectionFailures.StoredConnectionUnavailable();
        }

        var contextResult = DeploymentTargetConnectionEncryptionContext.TryCreate(
            deploymentTargetId,
            connectionVersion,
            KubeconfigValueName);
        if (!contextResult.TryGetValue(out var context, out var failure))
            return failure;

        try
        {
            return await encryption.DecryptAsync(encryptedKubeconfig, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ConnectionFailures.StoredConnectionUnavailable();
        }
    }

    public async ValueTask<Result<EncryptedValue>> ProtectServiceAccountCredentialAsync(
        string deploymentTargetId,
        long connectionVersion,
        ReadOnlyMemory<byte> canonicalServiceAccountCredential,
        CancellationToken cancellationToken = default)
    {
        if (canonicalServiceAccountCredential.IsEmpty ||
            canonicalServiceAccountCredential.Length > ConnectionPayloadLimits.MaximumServiceAccountCredentialBytes)
        {
            return ConnectionFailures.Validation(
                "serviceAccount",
                "The service-account credential payload has an invalid size.");
        }

        var contextResult = DeploymentTargetConnectionEncryptionContext.TryCreate(
            deploymentTargetId,
            connectionVersion,
            ServiceAccountCredentialValueName);
        if (!contextResult.TryGetValue(out var context, out var failure))
            return failure;

        try
        {
            return await encryption.EncryptAsync(
                canonicalServiceAccountCredential,
                context,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }
    }

    public async ValueTask<Result<byte[]>> UnprotectServiceAccountCredentialAsync(
        string deploymentTargetId,
        long connectionVersion,
        EncryptedValue encryptedCredential,
        CancellationToken cancellationToken = default)
    {
        if (!HasBoundedCiphertext(
                encryptedCredential,
                ConnectionPayloadLimits.MaximumServiceAccountCredentialBytes))
        {
            return ConnectionFailures.StoredConnectionUnavailable();
        }

        var contextResult = DeploymentTargetConnectionEncryptionContext.TryCreate(
            deploymentTargetId,
            connectionVersion,
            ServiceAccountCredentialValueName);
        if (!contextResult.TryGetValue(out var context, out var failure))
            return failure;

        try
        {
            return await encryption.DecryptAsync(encryptedCredential, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ConnectionFailures.StoredConnectionUnavailable();
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

    public static Result<EnvelopeEncryptionContext> TryCreate(
        string deploymentTargetId,
        long connectionVersion,
        string valueName)
    {
        if (string.IsNullOrWhiteSpace(deploymentTargetId) ||
            deploymentTargetId.Length > ConnectionPayloadLimits.MaximumDeploymentTargetIdCharacters ||
            connectionVersion < 1)
        {
            return ConnectionFailures.Validation(
                "deploymentTargetId",
                "The deployment-target encryption context is invalid.");
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
        catch (ArgumentException)
        {
            return ConnectionFailures.Validation(
                "deploymentTargetId",
                "The deployment-target encryption context is invalid.");
        }
    }

    public static EnvelopeEncryptionContext ForKubeconfig(
        string deploymentTargetId,
        long connectionVersion) =>
        Create(deploymentTargetId, connectionVersion, KubeconfigValueName);

    public static EnvelopeEncryptionContext ForServiceAccount(
        string deploymentTargetId,
        long connectionVersion) =>
        Create(deploymentTargetId, connectionVersion, ServiceAccountValueName);

    private static EnvelopeEncryptionContext Create(
        string deploymentTargetId,
        long connectionVersion,
        string valueName)
    {
        var result = TryCreate(deploymentTargetId, connectionVersion, valueName);
        if (result.TryGetValue(out var context, out _))
            return context;
        throw new InvalidOperationException("The stored deployment-target encryption context is invalid.");
    }
}
