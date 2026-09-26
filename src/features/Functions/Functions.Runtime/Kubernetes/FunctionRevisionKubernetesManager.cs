using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Security;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Kubernetes;

public interface IFunctionRevisionKubernetesManager
{
    Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ApplyAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default);
    Task<Result<Unit, FunctionRevisionKubernetesOperationError>> DeleteAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default);
    // Only non-serving bootstrap statuses may bypass the runtime coordination generation.
    // Provisioning and ready revisions are not routable or owned by the scaler.
    Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleProvisioningAsync(
        FunctionRevisionInKubernetes revision,
        int replicas,
        CancellationToken cancellationToken = default);
    Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleAsync(
        FunctionRevisionInKubernetes revision,
        int replicas,
        long coordinationGeneration,
        CancellationToken cancellationToken = default);
    Task<Result<FunctionReplicaState, FunctionRevisionKubernetesOperationError>> ReadReplicaStateAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default);
}

public sealed record FunctionReplicaState(
    int Desired,
    int Current,
    int Ready,
    int Available = 0,
    bool GenerationObserved = true,
    long CoordinationGeneration = 0);

public sealed class FunctionRevisionKubernetesManager(
    IKubernetesClientProvider kubernetesClientProvider,
    LookupDeploymentTarget lookupDeploymentTarget,
    IEnumerable<IFunctionAgentTrustBundleProvider> trustBundleProviders)
    : IFunctionRevisionKubernetesManager
{
    private readonly FunctionAgentTrustBundle? trustBundle =
        trustBundleProviders.SingleOrDefault()?.GetTrustBundle();

    public async Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ApplyAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default)
    {
        if (!await AllowsWorkloadsAsync(revision.Placement.DeploymentTargetId, cancellationToken))
            return EnforcementRequired();

        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return new Err<FunctionRevisionKubernetesOperationError>(clientFailure);

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
                    PreserveScaleGeneration(deployment, existing);
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
            return new Ok<Unit>(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Unable to apply Function revision in Kubernetes.");
        }
    }

    public async Task<Result<Unit, FunctionRevisionKubernetesOperationError>> DeleteAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default)
    {
        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return new Err<FunctionRevisionKubernetesOperationError>(clientFailure);

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
            return new Ok<Unit>(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Unable to delete Function revision from Kubernetes.");
        }
    }

    public async Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleProvisioningAsync(
        FunctionRevisionInKubernetes revision,
        int replicas,
        CancellationToken cancellationToken = default)
    {
        if (revision.Revision.Status is not (ProvisioningFunctionRevisionStatus or ReadyFunctionRevisionStatus))
        {
            return Rejected(
                "Only a provisioning or ready Function revision may use the unfenced bootstrap scale operation.");
        }
        if (replicas < 0 || replicas > revision.Revision.Spec.Scaling.MaxInstances)
        {
            return Invalid("Replica count is outside the Function revision scaling bounds.");
        }

        var allowsIncrease = replicas == 0 ||
            await AllowsWorkloadsAsync(revision.Placement.DeploymentTargetId, cancellationToken);
        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return new Err<FunctionRevisionKubernetesOperationError>(clientFailure);

        try
        {
            var scale = await kubernetes.AppsV1.ReadNamespacedDeploymentScaleAsync(
                revision.DeploymentName,
                revision.Namespace,
                cancellationToken: cancellationToken);
            if (replicas > (scale.Spec.Replicas ?? 0) && !allowsIncrease)
                return EnforcementRequired();
            scale.Spec.Replicas = replicas;
            await kubernetes.AppsV1.ReplaceNamespacedDeploymentScaleAsync(
                scale,
                revision.DeploymentName,
                revision.Namespace,
                cancellationToken: cancellationToken);
            return new Ok<Unit>(Unit.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Unable to scale Function revision in Kubernetes.");
        }
    }

    public async Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleAsync(
        FunctionRevisionInKubernetes revision,
        int replicas,
        long coordinationGeneration,
        CancellationToken cancellationToken = default)
    {
        if (replicas < 0 || replicas > revision.Revision.Spec.Scaling.MaxInstances)
        {
            return Invalid("Replica count is outside the Function revision scaling bounds.");
        }
        if (coordinationGeneration <= 0)
        {
            return Invalid("Scale coordination generation must be positive.");
        }

        var allowsIncrease = replicas == 0 ||
            await AllowsWorkloadsAsync(revision.Placement.DeploymentTargetId, cancellationToken);
        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return new Err<FunctionRevisionKubernetesOperationError>(clientFailure);

        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var deployment = await kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
                    revision.DeploymentName,
                    revision.Namespace,
                    cancellationToken: cancellationToken);
                if (!TryReadScaleGeneration(deployment, out var currentGeneration))
                {
                    return Rejected(
                        "The Function revision has an invalid Kubernetes scale generation annotation.");
                }
                if (currentGeneration > coordinationGeneration)
                {
                    return Rejected(
                        "A newer Function revision scale decision has already been applied.");
                }
                if (currentGeneration == coordinationGeneration)
                {
                    return deployment.Spec.Replicas == replicas
                        ? new Ok<Unit>(Unit.Value)
                        : Rejected(
                            "The Function revision scale generation is already associated with another replica count.");
                }

                if (replicas > (deployment.Spec.Replicas ?? 0) && !allowsIncrease)
                    return EnforcementRequired();
                deployment.Spec.Replicas = replicas;
                deployment.Metadata.Annotations ??= new Dictionary<string, string>();
                deployment.Metadata.Annotations[FunctionRevisionInKubernetes.ScaleGenerationAnnotation] =
                    coordinationGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    await kubernetes.AppsV1.ReplaceNamespacedDeploymentAsync(
                        deployment,
                        deployment.Metadata.Name,
                        revision.Namespace,
                        cancellationToken: cancellationToken);
                    return new Ok<Unit>(Unit.Value);
                }
                catch (HttpOperationException exception) when (
                    exception.Response?.StatusCode == HttpStatusCode.Conflict)
                {
                    // Another writer changed resourceVersion. Re-read both the generation and replicas
                    // before deciding whether this command is still current.
                }
            }

            return Rejected(
                "The Function revision changed repeatedly while applying its scale decision.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Unable to scale Function revision in Kubernetes.");
        }
    }

    public async Task<Result<FunctionReplicaState, FunctionRevisionKubernetesOperationError>> ReadReplicaStateAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default)
    {
        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return new Err<FunctionRevisionKubernetesOperationError>(clientFailure);

        try
        {
            var deployment = await kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
                revision.DeploymentName,
                revision.Namespace,
                cancellationToken: cancellationToken);
            return new Ok<FunctionReplicaState>(new FunctionReplicaState(
                deployment.Spec.Replicas ?? 0,
                deployment.Status?.Replicas ?? 0,
                deployment.Status?.ReadyReplicas ?? 0,
                deployment.Status?.AvailableReplicas ?? 0,
                deployment.Status?.ObservedGeneration >= deployment.Metadata.Generation,
                TryReadScaleGeneration(deployment, out var coordinationGeneration)
                    ? coordinationGeneration
                    : 0));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable("Unable to read Function revision replica state from Kubernetes.");
        }
    }

    // Function provisioning/activation/scaling also run outside ResourceDeployment reconciliation.
    // Guard the shared mutation boundary, not placement lookup or diagnostics. Never cache evidence.
    private async Task<bool> AllowsWorkloadsAsync(
        string targetId,
        CancellationToken cancellationToken)
    {
        var result = await lookupDeploymentTarget(targetId, cancellationToken);
        return result is DeploymentTargetLookupDto target &&
            target.NetworkPolicy.AllowsWorkloads;
    }

    private static Err<FunctionRevisionKubernetesOperationError> EnforcementRequired() =>
        Rejected("NetworkPolicy enforcement has not been established for this target.");

    private async Task<Result<IKubernetes, FunctionRevisionKubernetesOperationError>> ResolveKubernetesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken)
    {
        try
        {
            var kubernetes = await kubernetesClientProvider.GetAsync(
                deploymentTargetId,
                cancellationToken);
            return new Ok<IKubernetes>(kubernetes);
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            return Unavailable(
                $"The persisted Kubernetes deployment target is unavailable ({exception.Reason}).");
        }
    }

    private static Err<FunctionRevisionKubernetesOperationError> Unavailable(string message) =>
        new(new FunctionRevisionKubernetesUnavailable(message));

    private static Err<FunctionRevisionKubernetesOperationError> Rejected(string message) =>
        new(new FunctionRevisionKubernetesOperationRejected(message));

    private static Err<FunctionRevisionKubernetesOperationError> Invalid(string message) =>
        new(new FunctionRevisionKubernetesOperationInvalid(message));

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

    private static void PreserveScaleGeneration(V1Deployment desired, V1Deployment existing)
    {
        if (existing.Metadata.Annotations is null ||
            !existing.Metadata.Annotations.TryGetValue(
                FunctionRevisionInKubernetes.ScaleGenerationAnnotation,
                out var generation))
        {
            return;
        }
        desired.Metadata.Annotations ??= new Dictionary<string, string>();
        desired.Metadata.Annotations[FunctionRevisionInKubernetes.ScaleGenerationAnnotation] =
            generation;
    }

    private static bool TryReadScaleGeneration(V1Deployment deployment, out long generation)
    {
        generation = 0;
        return deployment.Metadata.Annotations is null ||
               !deployment.Metadata.Annotations.TryGetValue(
                   FunctionRevisionInKubernetes.ScaleGenerationAnnotation,
                   out var value) ||
               long.TryParse(
                   value,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out generation) && generation >= 0;
    }
}
