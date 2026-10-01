using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed record PrivateAccessWorkerCreated;
public sealed record PrivateAccessWorkerTargetUnavailable;
public sealed record PrivateAccessWorkerIsolationUnavailable;
public sealed record PrivateAccessWorkerOwnershipConflict;
public sealed record PrivateAccessWorkerDeleted;
public sealed record PrivateAccessWorkerCleanupPending;

public union DeletePrivateAccessWorkerOutcome(
    PrivateAccessWorkerDeleted,
    PrivateAccessWorkerCleanupPending);

public union CreatePrivateAccessWorkerOutcome(
    PrivateAccessWorkerCreated,
    PrivateAccessWorkerTargetUnavailable,
    PrivateAccessWorkerIsolationUnavailable,
    PrivateAccessWorkerOwnershipConflict);

public sealed class PrivateAccessWorkerResources(
    IKubernetesClientProvider clients,
    LookupDeploymentTarget lookupTarget,
    EnsureRuntimeCellIngressBaseline ensureBaseline)
{
    public async Task<CreatePrivateAccessWorkerOutcome> CreateAsync(
        string sessionId,
        PrivateAccessWorkerDestination destination,
        PrivateAccessWorkerSettings settings,
        string ticket,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var target = await lookupTarget(destination.DeploymentTargetId, cancellationToken);
        if (target is not DeploymentTargetLookupDto { Enabled: true, DeletionRequested: false } found)
            return new PrivateAccessWorkerTargetUnavailable();
        if (found.NetworkPolicy.Readiness != NetworkPolicyReadiness.EnforcementAttested ||
            settings.Namespace == destination.KubernetesNamespace)
            return new PrivateAccessWorkerIsolationUnavailable();
        var baseline = await ensureBaseline(destination.TenantId, destination.RuntimeCellId,
            destination.DeploymentTargetId, destination.KubernetesNamespace, cancellationToken);
        if (baseline is not RuntimeCellIngressBaselineReady)
            return new PrivateAccessWorkerIsolationUnavailable();

        var client = await clients.GetAsync(destination.DeploymentTargetId, cancellationToken);
        var desiredNamespace = PrivateAccessWorkerManifest.BuildNamespace(destination.DeploymentTargetId, settings);
        try
        {
            await client.CoreV1.CreateNamespaceAsync(desiredNamespace, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await client.CoreV1.ReadNamespaceAsync(settings.Namespace,
                cancellationToken: cancellationToken);
            if (existing.Metadata.Labels is not { } labels ||
                desiredNamespace.Metadata.Labels.Any(pair =>
                    !labels.TryGetValue(pair.Key, out var value) || value != pair.Value))
                return new PrivateAccessWorkerOwnershipConflict();
        }

        // Namespace isolation outlives every session policy and terminating pod.
        // Deleting a session allow-rule must never restore unrestricted egress.
        var baselinePolicy = PrivateAccessWorkerManifest.BuildWorkerBaseline(
            destination.DeploymentTargetId, settings.Namespace);
        try
        {
            await client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                baselinePolicy, settings.Namespace, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                PrivateAccessWorkerManifest.WorkerBaselineName, settings.Namespace,
                cancellationToken: cancellationToken);
            if (!MatchesWorkerBaseline(existing, destination.DeploymentTargetId))
                return new PrivateAccessWorkerIsolationUnavailable();
        }

        // Session IDs are unique. Create-only writes never adopt an object with conflicting
        // ownership. Policies are in place before a pod can start; partial setup is cleaned
        // through the same ownership-checked path as normal session termination.
        try
        {
            await client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                PrivateAccessWorkerManifest.BuildWorkerPolicy(sessionId, destination, settings, deadline),
                settings.Namespace, cancellationToken: cancellationToken);
            await client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                PrivateAccessWorkerManifest.BuildDestinationPolicy(sessionId, destination, settings, deadline),
                destination.KubernetesNamespace, cancellationToken: cancellationToken);
            var secret = new V1Secret
            {
                ApiVersion = "v1",
                Kind = "Secret",
                Type = "Opaque",
                Metadata = new V1ObjectMeta
                {
                    Name = PrivateAccessWorkerManifest.Name(sessionId),
                    NamespaceProperty = settings.Namespace,
                    Labels = new Dictionary<string, string>
                    {
                        [PrivateAccessWorkerManifest.SessionLabel] = sessionId,
                        [PrivateAccessWorkerManifest.TenantLabel] = destination.TenantId,
                        [PrivateAccessWorkerManifest.TargetLabel] = destination.DeploymentTargetId
                    }
                },
                StringData = new Dictionary<string, string> { ["ticket"] = ticket }
            };
            try
            {
                await client.CoreV1.CreateNamespacedSecretAsync(secret, settings.Namespace,
                    cancellationToken: cancellationToken);
            }
            catch (HttpOperationException error) when (error.Response.StatusCode != HttpStatusCode.Conflict)
            {
                // The Kubernetes exception carries the submitted Secret in its request.
                // Preserve an infrastructure failure without exposing the rendezvous ticket.
                throw new InvalidOperationException(
                    $"Private Access credential provisioning failed with HTTP {(int)error.Response.StatusCode}.");
            }
            await client.BatchV1.CreateNamespacedJobAsync(
                PrivateAccessWorkerManifest.BuildJob(sessionId, destination, settings, deadline),
                settings.Namespace, cancellationToken: cancellationToken);
            return new PrivateAccessWorkerCreated();
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return new PrivateAccessWorkerOwnershipConflict();
        }
    }

    private static bool MatchesWorkerBaseline(V1NetworkPolicy policy, string targetId) =>
        policy.Metadata.Labels is { } labels &&
        labels.TryGetValue(PrivateAccessWorkerManifest.TargetLabel, out var owner) && owner == targetId &&
        labels.TryGetValue(PrivateAccessWorkerManifest.WorkerNamespaceLabel, out var marker) && marker == "true" &&
        policy.Spec.PodSelector is { } selector &&
        (selector.MatchLabels?.Count ?? 0) == 0 &&
        (selector.MatchExpressions?.Count ?? 0) == 0 &&
        policy.Spec.PolicyTypes is { Count: 2 } types && types.Contains("Ingress") && types.Contains("Egress") &&
        (policy.Spec.Ingress?.Count ?? 0) == 0 &&
        (policy.Spec.Egress?.Count ?? 0) == 0;

    public async Task<DeletePrivateAccessWorkerOutcome> DeleteAsync(
        string sessionId,
        PrivateAccessWorkerDestination destination,
        string workerNamespace,
        CancellationToken cancellationToken)
    {
        var client = await clients.GetAsync(destination.DeploymentTargetId, cancellationToken);
        var name = PrivateAccessWorkerManifest.Name(sessionId);
        var complete = await DeleteOwnedAsync(
            async () => (await client.BatchV1.ReadNamespacedJobAsync(name, workerNamespace,
                cancellationToken: cancellationToken)).Metadata,
            options => client.BatchV1.DeleteNamespacedJobAsync(name, workerNamespace,
                body: options, cancellationToken: cancellationToken),
            sessionId, destination);
        complete &= await DeleteOwnedAsync(
            async () => (await client.CoreV1.ReadNamespacedSecretAsync(name, workerNamespace,
                cancellationToken: cancellationToken)).Metadata,
            options => client.CoreV1.DeleteNamespacedSecretAsync(name, workerNamespace,
                body: options, cancellationToken: cancellationToken),
            sessionId, destination);
        foreach (var namespaceName in new[] { workerNamespace, destination.KubernetesNamespace })
            complete &= await DeleteOwnedAsync(
                async () => (await client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(name, namespaceName,
                    cancellationToken: cancellationToken)).Metadata,
                options => client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(name, namespaceName,
                    body: options, cancellationToken: cancellationToken),
                sessionId, destination);
        return complete ? new PrivateAccessWorkerDeleted() : new PrivateAccessWorkerCleanupPending();
    }

    private static async Task<bool> DeleteOwnedAsync(
        Func<Task<V1ObjectMeta>> read,
        Func<V1DeleteOptions, Task> delete,
        string sessionId,
        PrivateAccessWorkerDestination destination)
    {
        try
        {
            var metadata = await read();
            if (metadata.Labels is not { } labels ||
                !labels.TryGetValue(PrivateAccessWorkerManifest.SessionLabel, out var session) || session != sessionId ||
                !labels.TryGetValue(PrivateAccessWorkerManifest.TenantLabel, out var tenant) || tenant != destination.TenantId ||
                !labels.TryGetValue(PrivateAccessWorkerManifest.TargetLabel, out var target) || target != destination.DeploymentTargetId)
                return true;
            if (string.IsNullOrEmpty(metadata.Uid) || string.IsNullOrEmpty(metadata.ResourceVersion))
                return false;
            await delete(new V1DeleteOptions
            {
                PropagationPolicy = "Background",
                Preconditions = new V1Preconditions { Uid = metadata.Uid, ResourceVersion = metadata.ResourceVersion }
            });
            return true;
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Conflict)
        {
            // Keep the durable cleanup claim and target credentials until a later attempt
            // proves this exact session's objects were deleted or are no longer present.
            return false;
        }
    }
}
