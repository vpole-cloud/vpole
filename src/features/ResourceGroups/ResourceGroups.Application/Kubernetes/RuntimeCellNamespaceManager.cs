using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

public interface IRuntimeCellNamespaceManager
{
    Task<Result> EnsureCreatedAsync(
        RuntimeCell runtimeCell,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(
        RuntimeCell runtimeCell,
        CancellationToken cancellationToken = default);
}

public sealed class KubernetesRuntimeCellNamespaceManager(
    IKubernetesClientProvider kubernetesClientProvider)
    : IRuntimeCellNamespaceManager
{
    public async Task<Result> EnsureCreatedAsync(
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
                return Result.Success();
            }
            catch (HttpOperationException exception)
                when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await kubernetes.CoreV1.ReadNamespaceAsync(
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
                return HasExpectedOwnership(existing, runtimeCell)
                    ? Result.Success()
                    : OwnershipConflict(runtimeCell.KubernetesNamespace);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            return DeploymentTargetFailure("create or verify", exception.Reason);
        }
        catch (HttpOperationException)
        {
            return KubernetesFailure("create or verify");
        }
    }

    public async Task<Result> DeleteAsync(
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
                return Result.Success();
            }

            if (!HasExpectedOwnership(existing, runtimeCell))
                return OwnershipConflict(runtimeCell.KubernetesNamespace);

            try
            {
                await kubernetes.CoreV1.DeleteNamespaceAsync(
                    runtimeCell.KubernetesNamespace,
                    cancellationToken: cancellationToken);
                return Result.Success();
            }
            catch (HttpOperationException exception)
                when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return Result.Success();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            return DeploymentTargetFailure("delete", exception.Reason);
        }
        catch (HttpOperationException)
        {
            return KubernetesFailure("delete");
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

    private static ConflictFailure OwnershipConflict(string namespaceName) =>
        Failures.Conflict(
            $"Kubernetes namespace '{namespaceName}' is not owned by the requested runtime cell.");

    private static ExternalServiceFailure KubernetesFailure(string operation) =>
        Failures.ExternalService(
            $"Unable to {operation} the runtime cell Kubernetes namespace.",
            "Kubernetes",
            "The Kubernetes namespace operation failed.");

    private static ExternalServiceFailure DeploymentTargetFailure(
        string operation,
        DeploymentTargetUnavailableReason reason) =>
        Failures.ExternalService(
            $"Unable to {operation} the runtime cell Kubernetes namespace.",
            "Kubernetes",
            $"The deployment target is unavailable ({reason}).");
}
