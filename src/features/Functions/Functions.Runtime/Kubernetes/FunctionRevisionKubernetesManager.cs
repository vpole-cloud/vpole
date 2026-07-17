using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.Functions.Runtime.Security;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Kubernetes;

public interface IFunctionRevisionKubernetesManager
{
    Task<Result> ApplyAsync(FunctionRevisionInKubernetes revision, CancellationToken cancellationToken = default);
    Task<Result> DeleteAsync(FunctionRevisionInKubernetes revision, CancellationToken cancellationToken = default);
    Task<Result> ScaleAsync(FunctionRevisionInKubernetes revision, int replicas, CancellationToken cancellationToken = default);
}

public sealed class FunctionRevisionKubernetesManager(
    IKubernetes kubernetes,
    IEnumerable<IFunctionAgentTrustBundleProvider> trustBundleProviders)
    : IFunctionRevisionKubernetesManager
{
    private readonly FunctionAgentTrustBundle? trustBundle =
        trustBundleProviders.SingleOrDefault()?.GetTrustBundle();

    public async Task<Result> ApplyAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var deployment = revision.ToDeployment(trustBundle);
            var service = revision.ToService();
            await CreateOrReplaceAsync(
                () => kubernetes.AppsV1.CreateNamespacedDeploymentAsync(
                    deployment,
                    revision.Namespace,
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
                        deployment.Metadata.Name,
                        revision.Namespace,
                        cancellationToken: cancellationToken);
                    deployment.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                    deployment.Spec.Replicas = existing.Spec.Replicas;
                    await kubernetes.AppsV1.ReplaceNamespacedDeploymentAsync(
                        deployment,
                        deployment.Metadata.Name,
                        revision.Namespace,
                        cancellationToken: cancellationToken);
                });
            await CreateOrReplaceAsync(
                () => kubernetes.CoreV1.CreateNamespacedServiceAsync(
                    service,
                    revision.Namespace,
                    cancellationToken: cancellationToken),
                async () =>
                {
                    var existing = await kubernetes.CoreV1.ReadNamespacedServiceAsync(
                        service.Metadata.Name,
                        revision.Namespace,
                        cancellationToken: cancellationToken);
                    PreserveServiceOwnedFields(service, existing);
                    await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(
                        service,
                        service.Metadata.Name,
                        revision.Namespace,
                        cancellationToken: cancellationToken);
                });
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failures.ExternalService(
                "Unable to apply Function revision in Kubernetes.",
                "Kubernetes",
                exception.Message);
        }
    }

    public async Task<Result> DeleteAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await KubernetesExtensions.DeleteIfExistsAsync(() =>
                kubernetes.CoreV1.DeleteNamespacedServiceAsync(
                    revision.ServiceName,
                    revision.Namespace,
                    cancellationToken: cancellationToken));
            await KubernetesExtensions.DeleteIfExistsAsync(() =>
                kubernetes.AppsV1.DeleteNamespacedDeploymentAsync(
                    revision.DeploymentName,
                    revision.Namespace,
                    cancellationToken: cancellationToken));
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failures.ExternalService(
                "Unable to delete Function revision from Kubernetes.",
                "Kubernetes",
                exception.Message);
        }
    }

    public async Task<Result> ScaleAsync(
        FunctionRevisionInKubernetes revision,
        int replicas,
        CancellationToken cancellationToken = default)
    {
        if (replicas < 0 || replicas > revision.Revision.Spec.Scaling.MaxInstances)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                [nameof(replicas)] = ["Replica count is outside the Function revision scaling bounds."]
            });
        }

        try
        {
            var scale = await kubernetes.AppsV1.ReadNamespacedDeploymentScaleAsync(
                revision.DeploymentName,
                revision.Namespace,
                cancellationToken: cancellationToken);
            scale.Spec.Replicas = replicas;
            await kubernetes.AppsV1.ReplaceNamespacedDeploymentScaleAsync(
                scale,
                revision.DeploymentName,
                revision.Namespace,
                cancellationToken: cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failures.ExternalService(
                "Unable to scale Function revision in Kubernetes.",
                "Kubernetes",
                exception.Message);
        }
    }

    private static async Task CreateOrReplaceAsync(Func<Task> create, Func<Task> replace)
    {
        try
        {
            await create();
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            await replace();
        }
    }

    private static void PreserveServiceOwnedFields(V1Service desired, V1Service existing)
    {
        desired.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
        desired.Spec.ClusterIP = existing.Spec.ClusterIP;
        desired.Spec.ClusterIPs = existing.Spec.ClusterIPs;
        desired.Spec.HealthCheckNodePort = existing.Spec.HealthCheckNodePort;
        desired.Spec.IpFamilies = existing.Spec.IpFamilies;
        desired.Spec.IpFamilyPolicy = existing.Spec.IpFamilyPolicy;
    }
}
