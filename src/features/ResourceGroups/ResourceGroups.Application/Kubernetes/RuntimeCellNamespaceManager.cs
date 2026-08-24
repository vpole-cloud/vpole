using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

public sealed record RuntimeCellNamespaceOwnershipConflict(string KubernetesNamespace);

public sealed record RuntimeCellNamespaceUnavailable(
    string KubernetesNamespace,
    bool DeletionPending = false);

public union RuntimeCellNamespaceOperationOutcome(
    Unit,
    RuntimeCellNamespaceOwnershipConflict,
    RuntimeCellNamespaceUnavailable);

public delegate Task<RuntimeCellNamespaceOperationOutcome> EnsureRuntimeCellNamespace(
    RuntimeCell runtimeCell,
    CancellationToken cancellationToken = default);

public delegate Task<RuntimeCellNamespaceOperationOutcome> DeleteRuntimeCellNamespace(
    RuntimeCell runtimeCell,
    CancellationToken cancellationToken = default);

public sealed class KubernetesRuntimeCellNamespaceManager(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<RuntimeCellNamespaceOperationOutcome> EnsureCreatedAsync(
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
                    RuntimeCellNamespaceManifestBuilder.Build(runtimeCell),
                    cancellationToken: cancellationToken);
                return Unit.Value;
            }
            catch (HttpOperationException exception)
                when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await kubernetes.CoreV1.ReadNamespaceAsync(
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
                return HasExpectedOwnership(existing, runtimeCell)
                    ? Unit.Value
                    : OwnershipConflict(runtimeCell.KubernetesNamespace);
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

    public async Task<RuntimeCellNamespaceOperationOutcome> DeleteAsync(
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
        var labels = existing.Metadata?.Labels;
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

    private static RuntimeCellNamespaceUnavailable DeletionPending(string namespaceName) =>
        new(namespaceName, DeletionPending: true);

    private static RuntimeCellNamespaceUnavailable DeploymentTargetFailure(
        string namespaceName) =>
        new(namespaceName);
}
