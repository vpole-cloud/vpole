using System.Security.Cryptography;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Connections;

public delegate ValueTask<Result<
    PreparedDeploymentTargetConnection,
    DeploymentTargetConnectionRejected>>
    PrepareDeploymentTargetConnection(
        string deploymentTargetId,
        long connectionVersion,
        DeploymentTargetConnectionInput input,
        CancellationToken cancellationToken = default);

internal sealed class DeploymentTargetConnectionPreparer(
    DeploymentTargetConnectionProtector protector)
{
    public async ValueTask<Result<
        PreparedDeploymentTargetConnection,
        DeploymentTargetConnectionRejected>> InvokeAsync(
        string deploymentTargetId,
        long connectionVersion,
        DeploymentTargetConnectionInput input,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareCoreAsync(
            deploymentTargetId,
            connectionVersion,
            input,
            cancellationToken);
        return prepared switch
        {
            Success<PreparedDeploymentTargetConnection> success =>
                new Ok<PreparedDeploymentTargetConnection>(success.Value),
            Failure<PreparedDeploymentTargetConnection>
            {
                Error: FieldValidationFailure validation
            } => new Err<DeploymentTargetConnectionRejected>(
                new DeploymentTargetConnectionRejected(validation.ValidationErrors)),
            Failure<PreparedDeploymentTargetConnection> => throw new InvalidOperationException(
                "Preparing a deployment-target connection failed outside its expected validation contract."),
            _ => throw new InvalidOperationException(
                "Preparing a deployment-target connection returned an unsupported result case.")
        };
    }

    private async ValueTask<Result<PreparedDeploymentTargetConnection>> PrepareCoreAsync(
        string deploymentTargetId,
        long connectionVersion,
        DeploymentTargetConnectionInput input,
        CancellationToken cancellationToken)
    {
        if (!HasValidOwner(deploymentTargetId, connectionVersion))
        {
            return ConnectionFailures.Validation(
                "deploymentTargetId",
                "The deployment-target connection owner is invalid.");
        }

        if (input is null)
        {
            return ConnectionFailures.Validation(
                "connection",
                "A deployment-target connection is required.");
        }

        return input switch
        {
            InClusterConnectionInput => PrepareInCluster(connectionVersion, cancellationToken),
            KubeconfigConnectionInput kubeconfig => await PrepareKubeconfigAsync(
                deploymentTargetId,
                connectionVersion,
                kubeconfig,
                cancellationToken),
            ServiceAccountConnectionInput serviceAccount => await PrepareServiceAccountAsync(
                deploymentTargetId,
                connectionVersion,
                serviceAccount,
                cancellationToken),
            _ => ConnectionFailures.Validation(
                "connection",
                "The deployment-target connection kind is unsupported.")
        };
    }

    private static Result<PreparedDeploymentTargetConnection> PrepareInCluster(
        long connectionVersion,
        CancellationToken cancellationToken)
    {
        var clientResult = KubernetesClientSecurity.CreateInCluster(
            storedConnection: false,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var failure))
            return failure;

        return new PreparedDeploymentTargetConnection(
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            client);
    }

    private async ValueTask<Result<PreparedDeploymentTargetConnection>> PrepareKubeconfigAsync(
        string deploymentTargetId,
        long connectionVersion,
        KubeconfigConnectionInput input,
        CancellationToken cancellationToken)
    {
        var canonicalResult = KubeconfigCanonicalizer.Canonicalize(
            input.Kubeconfig,
            input.ContextName,
            cancellationToken);
        if (!canonicalResult.TryGetValue(out var canonical, out var canonicalFailure))
            return canonicalFailure;

        var clientResult = KubernetesClientSecurity.CreateFromCanonical(
            canonical,
            storedConnection: false,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var clientFailure))
            return clientFailure;

        byte[] serialized;
        try
        {
            serialized = KubeconfigCanonicalizer.Serialize(canonical);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }

        try
        {
            var encryptedResult = await protector.ProtectKubeconfigAsync(
                deploymentTargetId,
                connectionVersion,
                serialized,
                cancellationToken);
            if (!encryptedResult.TryGetValue(out var encrypted, out var encryptionFailure))
            {
                client.Dispose();
                return encryptionFailure;
            }

            var persisted = DeploymentTargetConnectionFactory.CreateKubeconfig(
                connectionVersion,
                canonical.ContextName,
                canonical.ServerUrl,
                encrypted);
            return new PreparedDeploymentTargetConnection(persisted, client);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private async ValueTask<Result<PreparedDeploymentTargetConnection>> PrepareServiceAccountAsync(
        string deploymentTargetId,
        long connectionVersion,
        ServiceAccountConnectionInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalResult = KubeconfigCanonicalizer.CreateServiceAccountCredential(
            input.ServerUrl,
            input.CertificateAuthorityData,
            input.Token);
        if (!canonicalResult.TryGetValue(out var canonical, out var canonicalFailure))
            return canonicalFailure;

        var clientResult = KubernetesClientSecurity.CreateFromCanonical(
            canonical,
            storedConnection: false,
            cancellationToken);
        if (!clientResult.TryGetValue(out var client, out var clientFailure))
            return clientFailure;

        byte[] serialized;
        try
        {
            serialized = KubeconfigCanonicalizer.Serialize(canonical);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }

        try
        {
            var encryptedResult = await protector.ProtectServiceAccountCredentialAsync(
                deploymentTargetId,
                connectionVersion,
                serialized,
                cancellationToken);
            if (!encryptedResult.TryGetValue(out var encrypted, out var encryptionFailure))
            {
                client.Dispose();
                return encryptionFailure;
            }

            var persisted = DeploymentTargetConnectionFactory.CreateServiceAccount(
                connectionVersion,
                canonical.ServerUrl,
                canonical.CertificateAuthorityData,
                encrypted);
            return new PreparedDeploymentTargetConnection(persisted, client);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            client.Dispose();
            return ConnectionFailures.ConnectionProtectionUnavailable();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private static bool HasValidOwner(string? deploymentTargetId, long connectionVersion) =>
        !string.IsNullOrWhiteSpace(deploymentTargetId) &&
        deploymentTargetId.Length <= ConnectionPayloadLimits.MaximumDeploymentTargetIdCharacters &&
        connectionVersion > 0;
}
