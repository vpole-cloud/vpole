using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Secrets.Contracts;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Security;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.Functions.Runtime.Kubernetes;

public interface IFunctionRevisionKubernetesManager
{
    Task<FunctionRevisionKubernetesMutationOutcome> ApplyAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default);
    Task<FunctionRevisionKubernetesMutationOutcome> DeleteAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default);
    // Only non-serving bootstrap statuses may bypass the runtime coordination generation.
    // Provisioning and ready revisions are not routable or owned by the scaler.
    Task<FunctionRevisionKubernetesMutationOutcome> ScaleProvisioningAsync(
        FunctionRevisionInKubernetes revision,
        int replicas,
        CancellationToken cancellationToken = default);
    Task<FunctionRevisionKubernetesMutationOutcome> ScaleAsync(
        FunctionRevisionInKubernetes revision,
        int replicas,
        long coordinationGeneration,
        CancellationToken cancellationToken = default);
    Task<FunctionReplicaStateOutcome> ReadReplicaStateAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default);
}

public sealed record FunctionRevisionKubernetesMutationCompleted;

public union FunctionRevisionKubernetesMutationOutcome(
    FunctionRevisionKubernetesMutationCompleted,
    FunctionRevisionKubernetesOperationError);

public union FunctionReplicaStateOutcome(
    FunctionReplicaState,
    FunctionRevisionKubernetesOperationError);

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
    EnsureRuntimeCellIngressBaseline ensureRuntimeCellIngressBaseline,
    IEnumerable<IFunctionAgentTrustBundleProvider> trustBundleProviders,
    IServiceScopeFactory scopes)
    : IFunctionRevisionKubernetesManager
{
    private readonly FunctionAgentTrustBundle? trustBundle =
        trustBundleProviders.SingleOrDefault()?.GetTrustBundle();

    public async Task<FunctionRevisionKubernetesMutationOutcome> ApplyAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default)
    {
        var admission = await TargetWorkloadReadinessAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (admission is not TargetWorkloadReadiness.Ready)
            return AdmissionFailure(admission);

        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (clientResult is FunctionRevisionKubernetesOperationError clientFailure)
            return clientFailure;
        var kubernetes = (IKubernetes)clientResult.Value;
        if (!await BaselineReadyAsync(revision, cancellationToken))
            return EnforcementRequired();

        var projections = await ResolveSecretsAsync(revision, cancellationToken);
        if (projections is FunctionRevisionSecretsUnavailable unavailable)
            return new FunctionRevisionKubernetesOperationError(unavailable);

        try
        {
            var deployment = revision.ToDeployment(
                trustBundle, ((FunctionSecretProjections)projections.Value).Environment);
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
            return new FunctionRevisionKubernetesMutationCompleted();
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

    private async Task<FunctionSecretProjectionOutcome> ResolveSecretsAsync(
        FunctionRevisionInKubernetes subject,
        CancellationToken ct)
    {
        var references = subject.Revision.Spec.Configuration.SecretReferences;
        if (references.Count == 0)
            return new FunctionSecretProjections(new Dictionary<string, FunctionSecretProjection>());

        await using var scope = scopes.CreateAsyncScope();
        var resolve = scope.ServiceProvider.GetRequiredService<EnsureConsumerSecretProjections>();
        var result = await resolve(new EnsureConsumerSecretProjectionsRequest(
            subject.App.TenantId,
            FunctionResourceDeployment.ResourceType,
            subject.App.Id,
            subject.Placement.ResourceDeploymentId,
            references.Values.Select(value => new ConsumerSecretKeyReference(value.SecretId, value.Key))
                .Distinct().ToArray()), ct);
        if (result is not ConsumerSecretProjectionKeys resolved)
            return new FunctionRevisionSecretsUnavailable("Function Secret projections are not ready or authorized.");

        return new FunctionSecretProjections(references.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var projection = resolved.Keys.Single(item =>
                    item.SecretId == pair.Value.SecretId && item.SourceKey == pair.Value.Key);
                return new FunctionSecretProjection(projection.KubernetesName, projection.ProjectedKey);
            }, StringComparer.Ordinal));
    }

    public async Task<FunctionRevisionKubernetesMutationOutcome> DeleteAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default)
    {
        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (clientResult is FunctionRevisionKubernetesOperationError clientFailure)
            return clientFailure;
        var kubernetes = (IKubernetes)clientResult.Value;

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
            return new FunctionRevisionKubernetesMutationCompleted();
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

    public async Task<FunctionRevisionKubernetesMutationOutcome> ScaleProvisioningAsync(
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

        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (clientResult is FunctionRevisionKubernetesOperationError clientFailure)
            return clientFailure;
        var kubernetes = (IKubernetes)clientResult.Value;

        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scale = await kubernetes.AppsV1.ReadNamespacedDeploymentScaleAsync(
                    revision.DeploymentName,
                    revision.Namespace,
                    cancellationToken: cancellationToken);
                var currentReplicas = scale.Spec.Replicas ?? 0;
                if (replicas == currentReplicas)
                    return new FunctionRevisionKubernetesMutationCompleted();

                if (replicas > currentReplicas)
                {
                    var admission = await WorkloadReadinessAsync(revision, cancellationToken);
                    if (admission is not TargetWorkloadReadiness.Ready)
                        return AdmissionFailure(admission);
                }

                scale.Spec.Replicas = replicas;
                try
                {
                    await kubernetes.AppsV1.ReplaceNamespacedDeploymentScaleAsync(
                        scale,
                        revision.DeploymentName,
                        revision.Namespace,
                        cancellationToken: cancellationToken);
                    return new FunctionRevisionKubernetesMutationCompleted();
                }
                catch (HttpOperationException exception) when (
                    exception.Response?.StatusCode == HttpStatusCode.Conflict)
                {
                    // Controllers also change resourceVersion. Re-read the scale and recheck admission
                    // before attempting another increase; another writer may already have converged it.
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return Rejected("The Function revision changed repeatedly while applying its provisioning scale.");
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

    public async Task<FunctionRevisionKubernetesMutationOutcome> ScaleAsync(
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

        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (clientResult is FunctionRevisionKubernetesOperationError clientFailure)
            return clientFailure;
        var kubernetes = (IKubernetes)clientResult.Value;

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
                        ? new FunctionRevisionKubernetesMutationCompleted()
                        : Rejected(
                            "The Function revision scale generation is already associated with another replica count.");
                }

                if (replicas > (deployment.Spec.Replicas ?? 0))
                {
                    var admission = await WorkloadReadinessAsync(revision, cancellationToken);
                    if (admission is not TargetWorkloadReadiness.Ready)
                        return AdmissionFailure(admission);
                }
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
                    return new FunctionRevisionKubernetesMutationCompleted();
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

    public async Task<FunctionReplicaStateOutcome> ReadReplicaStateAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken = default)
    {
        var clientResult = await ResolveKubernetesAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (clientResult is FunctionRevisionKubernetesOperationError clientFailure)
            return clientFailure;
        var kubernetes = (IKubernetes)clientResult.Value;

        try
        {
            var deployment = await kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
                revision.DeploymentName,
                revision.Namespace,
                cancellationToken: cancellationToken);
            return new FunctionReplicaState(
                deployment.Spec.Replicas ?? 0,
                deployment.Status?.Replicas ?? 0,
                deployment.Status?.ReadyReplicas ?? 0,
                deployment.Status?.AvailableReplicas ?? 0,
                deployment.Status?.ObservedGeneration >= deployment.Metadata.Generation,
                TryReadScaleGeneration(deployment, out var coordinationGeneration)
                    ? coordinationGeneration
                    : 0);
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
    private async Task<TargetWorkloadReadiness> WorkloadReadinessAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken)
    {
        var targetReadiness = await TargetWorkloadReadinessAsync(
            revision.Placement.DeploymentTargetId,
            cancellationToken);
        if (targetReadiness is not TargetWorkloadReadiness.Ready)
            return targetReadiness;
        return await BaselineReadyAsync(revision, cancellationToken)
            ? TargetWorkloadReadiness.Ready
            : TargetWorkloadReadiness.BaselineUnavailable;
    }

    private async Task<TargetWorkloadReadiness> TargetWorkloadReadinessAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken)
    {
        var result = await lookupDeploymentTarget(
            deploymentTargetId,
            cancellationToken);
        if (result is not DeploymentTargetLookupDto target ||
            !target.NetworkPolicy.AllowsWorkloads)
            return TargetWorkloadReadiness.EnforcementRequired;
        return target.WorkloadConfiguration.NetworkSources.Get(FunctionResourceDeployment.GatewaySourceName) is null
            ? TargetWorkloadReadiness.GatewaySourceUnavailable
            : TargetWorkloadReadiness.Ready;
    }

    private async Task<bool> BaselineReadyAsync(
        FunctionRevisionInKubernetes revision,
        CancellationToken cancellationToken)
    {
        var baseline = await ensureRuntimeCellIngressBaseline(
            new EnsureRuntimeCellIngressBaselineRequest(
                TenantId: revision.App.TenantId,
                RuntimeCellId: revision.Placement.RuntimeCellId,
                DeploymentTargetId: revision.Placement.DeploymentTargetId,
                KubernetesNamespace: revision.Namespace),
            cancellationToken: cancellationToken);
        return baseline is RuntimeCellIngressBaselineReady;
    }

    private static FunctionRevisionKubernetesOperationError EnforcementRequired() =>
        Rejected("NetworkPolicy enforcement has not been established for this target.");

    private static FunctionRevisionKubernetesOperationError AdmissionFailure(
        TargetWorkloadReadiness readiness) => readiness switch
        {
            TargetWorkloadReadiness.GatewaySourceUnavailable =>
                new FunctionRevisionGatewaySourceUnavailable(
                    "The Function gateway source is not configured for this target."),
            TargetWorkloadReadiness.EnforcementRequired or TargetWorkloadReadiness.BaselineUnavailable =>
                EnforcementRequired(),
            _ => throw new ArgumentOutOfRangeException(nameof(readiness))
        };

    private enum TargetWorkloadReadiness
    {
        Ready,
        EnforcementRequired,
        GatewaySourceUnavailable,
        BaselineUnavailable
    }

    private async Task<ResolveKubernetesOutcome> ResolveKubernetesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken)
    {
        try
        {
            var kubernetes = await kubernetesClientProvider.GetAsync(
                deploymentTargetId,
                cancellationToken);
            return kubernetes;
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            return Unavailable(
                $"The persisted Kubernetes deployment target is unavailable ({exception.Reason}).");
        }
    }

    private static FunctionRevisionKubernetesOperationError Unavailable(string message) =>
        new FunctionRevisionKubernetesUnavailable(message);

    private static FunctionRevisionKubernetesOperationError Rejected(string message) =>
        new FunctionRevisionKubernetesOperationRejected(message);

    private static FunctionRevisionKubernetesOperationError Invalid(string message) =>
        new FunctionRevisionKubernetesOperationInvalid(message);

    private union ResolveKubernetesOutcome(
        IKubernetes,
        FunctionRevisionKubernetesOperationError);

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

internal sealed record FunctionSecretProjections(IReadOnlyDictionary<string, FunctionSecretProjection> Environment);

internal union FunctionSecretProjectionOutcome(
    FunctionSecretProjections,
    FunctionRevisionSecretsUnavailable);
