using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

public sealed record RuntimeCellNamespaceOwnershipConflict(string KubernetesNamespace);

public sealed record RuntimeCellNamespaceUnavailable(string KubernetesNamespace);

public sealed record RuntimeCellDeploymentTargetUnavailable(
    string KubernetesNamespace,
    DeploymentTargetUnavailableReason Reason);

public sealed record RuntimeCellNamespaceDeletionPending(string KubernetesNamespace);

public sealed record RuntimeCellNetworkPolicyOwnershipConflict(string KubernetesNamespace);

public sealed record RuntimeCellNetworkPolicyUnavailable(string KubernetesNamespace);

public union RuntimeCellEnsureOutcome(
    Unit,
    RuntimeCellNamespaceOwnershipConflict,
    RuntimeCellNamespaceUnavailable,
    RuntimeCellDeploymentTargetUnavailable,
    RuntimeCellNetworkPolicyOwnershipConflict,
    RuntimeCellNetworkPolicyUnavailable);

public union RuntimeCellDeleteOutcome(
    Unit,
    RuntimeCellNamespaceOwnershipConflict,
    RuntimeCellNamespaceUnavailable,
    RuntimeCellNamespaceDeletionPending);

public delegate Task<RuntimeCellEnsureOutcome> EnsureRuntimeCellNamespace(
    RuntimeCell runtimeCell,
    CancellationToken cancellationToken = default);

public delegate Task<RuntimeCellDeleteOutcome> DeleteRuntimeCellNamespace(
    RuntimeCell runtimeCell,
    CancellationToken cancellationToken = default);

public sealed class KubernetesRuntimeCellNamespaceManager(
    IKubernetesClientProvider kubernetesClientProvider,
    KubernetesOptions? kubernetesOptions = null)
{
    public async Task<RuntimeCellEnsureOutcome> EnsureCreatedAsync(
        RuntimeCell runtimeCell,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeCell);

        try
        {
            var kubernetes = await kubernetesClientProvider.GetAsync(
                runtimeCell.DeploymentTargetId,
                cancellationToken);
            try
            {
                await kubernetes.CoreV1.CreateNamespaceAsync(
                    RuntimeCellNamespaceManifestBuilder.Build(
                        runtimeCell,
                        kubernetesOptions?.AdditionalNamespaceLabels),
                    cancellationToken: cancellationToken);
            }
            catch (HttpOperationException exception)
                when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await kubernetes.CoreV1.ReadNamespaceAsync(
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
                if (!HasExpectedOwnership(existing, runtimeCell))
                    return OwnershipConflict(runtimeCell.KubernetesNamespace);
            }

            return await EnsureNetworkPolicyAsync(kubernetes, runtimeCell, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            return new RuntimeCellDeploymentTargetUnavailable(
                runtimeCell.KubernetesNamespace,
                exception.Reason);
        }
        catch (HttpOperationException)
        {
            return KubernetesFailure(runtimeCell.KubernetesNamespace);
        }
    }

    private static async Task<RuntimeCellEnsureOutcome> EnsureNetworkPolicyAsync(
        IKubernetes kubernetes,
        RuntimeCell runtimeCell,
        CancellationToken cancellationToken)
    {
        var desired = RuntimeCellNetworkPolicyManifestBuilder.Build(runtimeCell);
        try
        {
            try
            {
                await kubernetes.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                    desired,
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
                return Unit.Value;
            }
            catch (HttpOperationException exception)
                when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await kubernetes.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                    RuntimeCellNetworkPolicyManifestBuilder.Name,
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
                if (!HasExpectedOwnership(existing.Metadata?.Labels, runtimeCell))
                    return new RuntimeCellNetworkPolicyOwnershipConflict(runtimeCell.KubernetesNamespace);

                desired.Metadata.ResourceVersion = existing.Metadata!.ResourceVersion;
                await kubernetes.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(
                    desired,
                    RuntimeCellNetworkPolicyManifestBuilder.Name,
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
                return Unit.Value;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException)
        {
            return new RuntimeCellNetworkPolicyUnavailable(runtimeCell.KubernetesNamespace);
        }
    }

    public async Task<RuntimeCellDeleteOutcome> DeleteAsync(
        RuntimeCell runtimeCell,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeCell);

        try
        {
            var kubernetes = await kubernetesClientProvider.GetAsync(
                runtimeCell.DeploymentTargetId,
                cancellationToken);
            V1Namespace existing;
            try
            {
                existing = await kubernetes.CoreV1.ReadNamespaceAsync(
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
            }
            catch (HttpOperationException exception)
                when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return Unit.Value;
            }

            if (!HasExpectedOwnership(existing, runtimeCell))
                return OwnershipConflict(runtimeCell.KubernetesNamespace);

            try
            {
                await kubernetes.CoreV1.DeleteNamespaceAsync(
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
            }
            catch (HttpOperationException exception)
                when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return Unit.Value;
            }

            try
            {
                var remaining = await kubernetes.CoreV1.ReadNamespaceAsync(
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
                return HasExpectedOwnership(remaining, runtimeCell)
                    ? DeletionPending(runtimeCell.KubernetesNamespace)
                    : OwnershipConflict(runtimeCell.KubernetesNamespace);
            }
            catch (HttpOperationException exception)
                when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return Unit.Value;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentTargetUnavailableException)
        {
            return DeploymentTargetFailure(runtimeCell.KubernetesNamespace);
        }
        catch (HttpOperationException)
        {
            return KubernetesFailure(runtimeCell.KubernetesNamespace);
        }
    }

    private static bool HasExpectedOwnership(
        V1Namespace existing,
        RuntimeCell runtimeCell)
    {
        return HasExpectedOwnership(existing.Metadata?.Labels, runtimeCell);
    }

    private static bool HasExpectedOwnership(
        IDictionary<string, string>? labels,
        RuntimeCell runtimeCell)
    {
        if (labels is null)
            return false;

        return RuntimeCellNamespaceManifestBuilder.ExpectedOwnershipLabels(runtimeCell)
            .All(expected =>
                labels.TryGetValue(expected.Key, out var actual) &&
                string.Equals(actual, expected.Value, StringComparison.Ordinal));
    }

    private static RuntimeCellNamespaceOwnershipConflict OwnershipConflict(
        string namespaceName) =>
        new(namespaceName);

    private static RuntimeCellNamespaceUnavailable KubernetesFailure(string namespaceName) =>
        new(namespaceName);

    private static RuntimeCellNamespaceDeletionPending DeletionPending(string namespaceName) =>
        new(namespaceName);

    private static RuntimeCellNamespaceUnavailable DeploymentTargetFailure(
        string namespaceName) =>
        new(namespaceName);
}
