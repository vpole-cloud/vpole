using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed record PrivateAccessGatewayReady(
    string CertificateSha256Pin,
    DateTimeOffset CertificateNotAfter);
public sealed record PrivateAccessGatewayTargetUnavailable;
public sealed record PrivateAccessGatewayIsolationUnavailable;
public sealed record PrivateAccessGatewayOwnershipConflict;
public sealed record PrivateAccessGatewayCredentialMissing;
public sealed record PrivateAccessGatewayDeleted;
public sealed record PrivateAccessGatewayCleanupPending;

public union EnsurePrivateAccessGatewayOutcome(
    PrivateAccessGatewayReady,
    PrivateAccessGatewayTargetUnavailable,
    PrivateAccessGatewayIsolationUnavailable,
    PrivateAccessGatewayOwnershipConflict,
    PrivateAccessGatewayCredentialMissing);

public union DeletePrivateAccessGatewayOutcome(
    PrivateAccessGatewayDeleted,
    PrivateAccessGatewayCleanupPending,
    PrivateAccessGatewayOwnershipConflict);

public sealed class PrivateAccessGatewayResources(
    IKubernetesClientProvider clients,
    LookupDeploymentTarget lookupTarget,
    EnsureRuntimeCellIngressBaseline ensureBaseline)
{
    public async Task<EnsurePrivateAccessGatewayOutcome> EnsureAsync(
        PrivateAccessGatewayIdentity identity,
        IReadOnlyList<PrivateAccessGatewayEndpoint> endpoints,
        PrivateAccessGatewaySettings settings,
        byte[]? proposedPfx,
        CancellationToken cancellationToken)
    {
        var namespaceName = PrivateAccessGatewayManifest.Namespace(identity);
        if (identity.Generation < 1 || string.IsNullOrWhiteSpace(identity.GatewayId) ||
            endpoints.Select(endpoint => endpoint.EndpointId).Distinct(StringComparer.Ordinal).Count() != endpoints.Count ||
            endpoints.Any(endpoint => endpoint.KubernetesNamespace == namespaceName))
            throw new ArgumentException("Invalid gateway identity or endpoint set.");
        if (proposedPfx is not null && ReadCertificate(proposedPfx) is null)
            return new PrivateAccessGatewayCredentialMissing();

        var target = await lookupTarget(identity.DeploymentTargetId, cancellationToken);
        if (target is not DeploymentTargetLookupDto { Enabled: true, DeletionRequested: false } found)
            return new PrivateAccessGatewayTargetUnavailable();
        if (found.NetworkPolicy.Readiness != NetworkPolicyReadiness.EnforcementAttested)
            return new PrivateAccessGatewayIsolationUnavailable();

        foreach (var endpoint in endpoints)
        {
            var baseline = await ensureBaseline(identity.TenantId, endpoint.RuntimeCellId,
                identity.DeploymentTargetId, endpoint.KubernetesNamespace, cancellationToken);
            if (baseline is not RuntimeCellIngressBaselineReady)
                return new PrivateAccessGatewayIsolationUnavailable();
        }

        var client = await clients.GetAsync(identity.DeploymentTargetId, cancellationToken);
        var desiredNamespace = PrivateAccessGatewayManifest.BuildNamespace(identity);
        try
        {
            await client.CoreV1.CreateNamespaceAsync(desiredNamespace, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await client.CoreV1.ReadNamespaceAsync(namespaceName, cancellationToken: cancellationToken);
            if (!HasLabels(existing.Metadata, desiredNamespace.Metadata.Labels))
                return new PrivateAccessGatewayOwnershipConflict();
        }

        var baselinePolicy = PrivateAccessGatewayManifest.BuildBaseline(identity);
        if (!await EnsureOwnedAsync(
                () => client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                    baselinePolicy, namespaceName, cancellationToken: cancellationToken),
                () => client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                    baselinePolicy.Metadata.Name, namespaceName, cancellationToken: cancellationToken),
                existing =>
                {
                    baselinePolicy.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    return client.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(
                        baselinePolicy, baselinePolicy.Metadata.Name, namespaceName, cancellationToken: cancellationToken);
                },
                () => baselinePolicy.Metadata.ResourceVersion = null,
                baselinePolicy.Metadata.Labels,
                existing => IsDefaultDeny(existing.Spec)))
            return new PrivateAccessGatewayOwnershipConflict();

        foreach (var endpoint in endpoints)
        {
            var policy = PrivateAccessGatewayManifest.BuildDestinationIngress(identity, endpoint);
            if (!await EnsureOwnedAsync(
                    () => client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                        policy, endpoint.KubernetesNamespace, cancellationToken: cancellationToken),
                    () => client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                        policy.Metadata.Name, endpoint.KubernetesNamespace, cancellationToken: cancellationToken),
                    existing =>
                    {
                        policy.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        return client.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(
                            policy, policy.Metadata.Name, endpoint.KubernetesNamespace, cancellationToken: cancellationToken);
                    },
                    () => policy.Metadata.ResourceVersion = null,
                    policy.Metadata.Labels))
                return new PrivateAccessGatewayOwnershipConflict();
        }

        var egress = PrivateAccessGatewayManifest.BuildGatewayEgress(identity, endpoints, settings);
        if (!await EnsureOwnedAsync(
                () => client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                    egress, namespaceName, cancellationToken: cancellationToken),
                () => client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                    egress.Metadata.Name, namespaceName, cancellationToken: cancellationToken),
                existing =>
                {
                    egress.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    return client.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(
                        egress, egress.Metadata.Name, namespaceName, cancellationToken: cancellationToken);
                },
                () => egress.Metadata.ResourceVersion = null,
                egress.Metadata.Labels))
            return new PrivateAccessGatewayOwnershipConflict();

        var credential = await EnsureIdentitySecretAsync(client, identity, proposedPfx, cancellationToken);
        if (credential is null)
            return new PrivateAccessGatewayOwnershipConflict();
        if (credential.Length == 0)
            return new PrivateAccessGatewayCredentialMissing();

        var ready = ReadCertificate(credential);
        if (ready is null)
            return new PrivateAccessGatewayCredentialMissing();

        var deployment = PrivateAccessGatewayManifest.BuildDeployment(identity, settings);
        if (!await EnsureOwnedAsync(
                () => client.AppsV1.CreateNamespacedDeploymentAsync(
                    deployment, namespaceName, cancellationToken: cancellationToken),
                () => client.AppsV1.ReadNamespacedDeploymentAsync(
                    deployment.Metadata.Name, namespaceName, cancellationToken: cancellationToken),
                existing =>
                {
                    deployment.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    return client.AppsV1.ReplaceNamespacedDeploymentAsync(
                        deployment, deployment.Metadata.Name, namespaceName, cancellationToken: cancellationToken);
                },
                () => deployment.Metadata.ResourceVersion = null,
                deployment.Metadata.Labels,
                existing => existing.Spec.Selector?.MatchLabels is { } selector &&
                    selector.Count == deployment.Spec.Selector.MatchLabels.Count &&
                    HasLabels(selector, deployment.Spec.Selector.MatchLabels)))
            return new PrivateAccessGatewayOwnershipConflict();

        return ready;
    }

    private static PrivateAccessGatewayReady? ReadCertificate(byte[] pfx)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12(
                pfx, null, X509KeyStorageFlags.EphemeralKeySet);
            return certificate.HasPrivateKey && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow
                ? new PrivateAccessGatewayReady(
                    certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(),
                    new DateTimeOffset(certificate.NotAfter.ToUniversalTime()))
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> EnsureIdentitySecretAsync(
        IKubernetes client,
        PrivateAccessGatewayIdentity identity,
        byte[]? proposedPfx,
        CancellationToken cancellationToken)
    {
        var namespaceName = PrivateAccessGatewayManifest.Namespace(identity);
        var desired = proposedPfx is null ? null :
            PrivateAccessGatewayManifest.BuildIdentitySecret(identity, proposedPfx);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (desired is not null)
            {
                try
                {
                    await client.CoreV1.CreateNamespacedSecretAsync(
                        desired, namespaceName, cancellationToken: cancellationToken);
                    return proposedPfx;
                }
                catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.Conflict)
                {
                    // An earlier attempt may have installed this identity before registration committed.
                }
                catch (HttpOperationException error)
                {
                    throw new InvalidOperationException(
                        $"Gateway identity Secret write failed with HTTP {(int?)error.Response?.StatusCode}.");
                }
            }

            V1Secret existing;
            try
            {
                existing = await client.CoreV1.ReadNamespacedSecretAsync(
                    PrivateAccessGatewayManifest.GatewayName, namespaceName, cancellationToken: cancellationToken);
            }
            catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                if (desired is null)
                    return [];
                continue;
            }
            catch (HttpOperationException error)
            {
                throw new InvalidOperationException(
                    $"Gateway identity Secret read failed with HTTP {(int?)error.Response?.StatusCode}.");
            }

            if (!HasLabels(existing.Metadata, PrivateAccessGatewayManifest.Labels(identity)))
                return null;
            if (existing.Metadata.Annotations is not { } annotations ||
                !annotations.TryGetValue(PrivateAccessGatewayManifest.GenerationAnnotation, out var text) ||
                !long.TryParse(text, out var existingGeneration) || existingGeneration > identity.Generation)
                return null;
            if (existingGeneration == identity.Generation)
                return existing.Data is { } data && data.TryGetValue("identity.pfx", out var value)
                    ? value : [];
            if (desired is null)
                return [];

            desired.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            try
            {
                await client.CoreV1.ReplaceNamespacedSecretAsync(
                    desired, PrivateAccessGatewayManifest.GatewayName, namespaceName,
                    cancellationToken: cancellationToken);
                return proposedPfx;
            }
            catch (HttpOperationException error) when (error.Response?.StatusCode is
                HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                desired.Metadata.ResourceVersion = null;
            }
            catch (HttpOperationException error)
            {
                throw new InvalidOperationException(
                    $"Gateway identity Secret rotation failed with HTTP {(int?)error.Response?.StatusCode}.");
            }
        }
        return null;
    }

    private static async Task<bool> EnsureOwnedAsync<T>(
        Func<Task<T>> create,
        Func<Task<T>> read,
        Func<T, Task<T>> replace,
        Action clearResourceVersion,
        IDictionary<string, string> expectedLabels,
        Func<T, bool>? verify = null) where T : IKubernetesObject<V1ObjectMeta>
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            clearResourceVersion();
            try
            {
                await create();
                return true;
            }
            catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.Conflict)
            {
                T existing;
                try
                {
                    existing = await read();
                }
                catch (HttpOperationException readError) when (readError.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }
                if (!HasLabels(existing.Metadata, expectedLabels) || verify is not null && !verify(existing))
                    return false;
                try
                {
                    await replace(existing);
                    return true;
                }
                catch (HttpOperationException replaceError) when (replaceError.Response?.StatusCode is
                    HttpStatusCode.Conflict or HttpStatusCode.NotFound)
                {
                    // Retry against a fresh resource version.
                }
            }
        }
        return false;
    }

    private static bool HasLabels(V1ObjectMeta? metadata, IDictionary<string, string> expected) =>
        metadata is not null && HasLabels(metadata.Labels, expected);

    private static bool HasLabels(IDictionary<string, string>? actual, IDictionary<string, string> expected) =>
        actual is not null && expected.All(pair =>
            actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool IsDefaultDeny(V1NetworkPolicySpec spec) =>
        (spec.PodSelector.MatchLabels?.Count ?? 0) == 0 &&
        (spec.PodSelector.MatchExpressions?.Count ?? 0) == 0 &&
        spec.PolicyTypes is { Count: 2 } types && types.Contains("Ingress") && types.Contains("Egress") &&
        (spec.Ingress?.Count ?? 0) == 0 && (spec.Egress?.Count ?? 0) == 0;

    public async Task<DeletePrivateAccessGatewayOutcome> DeleteEndpointAsync(
        PrivateAccessGatewayIdentity identity,
        PrivateAccessGatewayEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var client = await clients.GetAsync(identity.DeploymentTargetId, cancellationToken);
        var policy = PrivateAccessGatewayManifest.BuildDestinationIngress(identity, endpoint);
        var result = await DeleteOwnedAsync(
            () => client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                policy.Metadata.Name, endpoint.KubernetesNamespace, cancellationToken: cancellationToken),
            options => client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
                policy.Metadata.Name, endpoint.KubernetesNamespace, body: options,
                cancellationToken: cancellationToken),
            policy.Metadata.Labels);
        return result switch
        {
            DeleteResult.Deleted => new PrivateAccessGatewayDeleted(),
            DeleteResult.Pending => new PrivateAccessGatewayCleanupPending(),
            DeleteResult.OwnershipConflict => new PrivateAccessGatewayOwnershipConflict(),
            _ => throw new InvalidOperationException("Unknown gateway cleanup result.")
        };
    }

    public async Task<DeletePrivateAccessGatewayOutcome> DeleteAsync(
        PrivateAccessGatewayIdentity identity,
        IReadOnlyList<PrivateAccessGatewayEndpoint> knownEndpoints,
        CancellationToken cancellationToken)
    {
        var client = await clients.GetAsync(identity.DeploymentTargetId, cancellationToken);
        var namespaceName = PrivateAccessGatewayManifest.Namespace(identity);
        var labels = PrivateAccessGatewayManifest.Labels(identity);
        var results = new List<DeleteResult>
        {
            await DeleteOwnedAsync(
                () => client.AppsV1.ReadNamespacedDeploymentAsync(
                    PrivateAccessGatewayManifest.GatewayName, namespaceName, cancellationToken: cancellationToken),
                options => client.AppsV1.DeleteNamespacedDeploymentAsync(
                    PrivateAccessGatewayManifest.GatewayName, namespaceName, body: options,
                    cancellationToken: cancellationToken),
                labels),
            await DeleteOwnedAsync(
                () => client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                    PrivateAccessGatewayManifest.GatewayName, namespaceName, cancellationToken: cancellationToken),
                options => client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
                    PrivateAccessGatewayManifest.GatewayName, namespaceName, body: options,
                    cancellationToken: cancellationToken),
                labels)
        };
        foreach (var endpoint in knownEndpoints)
        {
            var policy = PrivateAccessGatewayManifest.BuildDestinationIngress(identity, endpoint);
            results.Add(await DeleteOwnedAsync(
                () => client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                    policy.Metadata.Name, endpoint.KubernetesNamespace, cancellationToken: cancellationToken),
                options => client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
                    policy.Metadata.Name, endpoint.KubernetesNamespace, body: options,
                    cancellationToken: cancellationToken),
                policy.Metadata.Labels));
        }
        results.Add(await DeleteOwnedAsync(
            () => client.CoreV1.ReadNamespacedSecretAsync(
                PrivateAccessGatewayManifest.GatewayName, namespaceName, cancellationToken: cancellationToken),
            options => client.CoreV1.DeleteNamespacedSecretAsync(
                PrivateAccessGatewayManifest.GatewayName, namespaceName, body: options,
                cancellationToken: cancellationToken),
            labels));

        if (results.Contains(DeleteResult.OwnershipConflict))
            return new PrivateAccessGatewayOwnershipConflict();
        return results.Contains(DeleteResult.Pending)
            ? new PrivateAccessGatewayCleanupPending()
            : new PrivateAccessGatewayDeleted();
    }

    private enum DeleteResult { Deleted, Pending, OwnershipConflict }

    private static async Task<DeleteResult> DeleteOwnedAsync<T>(
        Func<Task<T>> read,
        Func<V1DeleteOptions, Task> delete,
        IDictionary<string, string> expectedLabels) where T : IKubernetesObject<V1ObjectMeta>
    {
        try
        {
            var existing = await read();
            if (!HasLabels(existing.Metadata, expectedLabels))
                return DeleteResult.OwnershipConflict;
            if (string.IsNullOrEmpty(existing.Metadata.Uid) ||
                string.IsNullOrEmpty(existing.Metadata.ResourceVersion))
                return DeleteResult.Pending;
            await delete(new V1DeleteOptions
            {
                PropagationPolicy = "Background",
                Preconditions = new V1Preconditions
                {
                    Uid = existing.Metadata.Uid,
                    ResourceVersion = existing.Metadata.ResourceVersion
                }
            });
            return DeleteResult.Deleted;
        }
        catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return DeleteResult.Deleted;
        }
        catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            return DeleteResult.Pending;
        }
    }
}
