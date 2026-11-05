using System.Globalization;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Placements;
using Pole.Features.ResourceDeployments.Application.Planning;
using Pole.Features.ResourceDeployments.Application.Queries;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Kubernetes;

internal sealed class KubernetesResourceIngressPolicyManager(
    LoadIngressPolicyDeployment loadDeployment,
    LookupDeploymentTarget lookupDeploymentTarget,
    EnsureRuntimeCellIngressBaseline ensureRuntimeCellIngressBaseline,
    IKubernetesClientProvider kubernetesClientProvider,
    ResourceIngressPolicyCatalog catalog)
{
    private const int MaximumWriteAttempts = 3;

    public async Task<ReconcileResourceIngressPolicyOutcome> InvokeAsync(
        ResourceIngressPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var definition = catalog.Resolve(request);
        ValidateRequest(request, definition);

        var deployment = await loadDeployment(request, cancellationToken);
        if (deployment is null ||
            deployment.DesiredGeneration != request.DeploymentDesiredGeneration ||
            deployment.State is DeletedResourceDeploymentState ||
            deployment.DesiredOperation == ResourceDeploymentOperation.Delete &&
            request.AllowedTcpPorts.Count > 0)
        {
            return new ResourceIngressPolicyStaleRevision();
        }

        if (deployment.RuntimeCellId != request.RuntimeCellId ||
            deployment.ResolvedPlacement.DeploymentTargetId != request.DeploymentTargetId ||
            deployment.ResolvedPlacement.KubernetesNamespace != request.KubernetesNamespace)
        {
            return new ResourceIngressPolicyPlacementMismatch();
        }

        var targetOutcome = await lookupDeploymentTarget(request.DeploymentTargetId, cancellationToken);
        if (targetOutcome is not DeploymentTargetLookupDto target)
            return new ResourceIngressPolicyUnavailable();

        var targetRevision = target.WorkloadConfiguration.Revision;
        if (request.TargetWorkloadConfigurationRevision > targetRevision)
            return new ResourceIngressPolicyStaleRevision();

        var source = definition.ResolveSource(request, TargetSources(target));
        var publicStateChanged = definition.RequiresIngressPlacement &&
                                 request.AllowedTcpPorts.Count > 0 &&
                                 deployment.ResolvedPlacement.Ingress is null;
        var sourceUnavailable = request.AllowedTcpPorts.Count > 0 && source is null;
        var allowTraffic = !publicStateChanged && !sourceUnavailable &&
                           request.AllowedTcpPorts.Count > 0;

        if (allowTraffic)
        {
            if (!target.NetworkPolicy.AllowsWorkloads)
                return new ResourceIngressPolicyUnavailable();

            var baseline = await ensureRuntimeCellIngressBaseline(
                new EnsureRuntimeCellIngressBaselineRequest(
                    TenantId: request.TenantId,
                    RuntimeCellId: request.RuntimeCellId,
                    DeploymentTargetId: request.DeploymentTargetId,
                    KubernetesNamespace: request.KubernetesNamespace),
                cancellationToken: cancellationToken);
            ReconcileResourceIngressPolicyOutcome? baselineFailure = baseline switch
            {
                null => throw new InvalidOperationException("The ingress baseline returned no outcome."),
                RuntimeCellIngressBaselineReady => null,
                RuntimeCellIngressBaselinePlacementMismatch => new ResourceIngressPolicyPlacementMismatch(),
                RuntimeCellIngressBaselineNotReady or
                RuntimeCellIngressBaselineNamespaceOwnershipConflict or
                RuntimeCellIngressBaselineNamespaceUnavailable or
                RuntimeCellIngressBaselinePolicyOwnershipConflict or
                RuntimeCellIngressBaselinePolicyUnavailable or
                RuntimeCellIngressBaselineTargetNotFound or
                RuntimeCellIngressBaselineTargetDisabled or
                RuntimeCellIngressBaselineDefaultTargetNotConfigured or
                RuntimeCellIngressBaselineTargetConnectionUnavailable => new ResourceIngressPolicyUnavailable()
            };
            if (baselineFailure is { } failure)
                return failure;
        }

        var current = request with
        {
            TargetWorkloadConfigurationRevision = targetRevision,
            AllowedTcpPorts = allowTraffic ? request.AllowedTcpPorts : []
        };
        var desired = ResourceIngressPolicyManifestBuilder.Build(
            current,
            allowTraffic ? source : null,
            definition);

        try
        {
            var kubernetes = await kubernetesClientProvider.GetAsync(
                request.DeploymentTargetId,
                cancellationToken);
            for (var attempt = 0; attempt < MaximumWriteAttempts; attempt++)
            {
                if (!await StillCurrentAsync(request, targetRevision, allowTraffic, cancellationToken))
                    return new ResourceIngressPolicyStaleRevision();
                try
                {
                    await kubernetes.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                        desired,
                        request.KubernetesNamespace,
                        cancellationToken: cancellationToken);
                    return OutcomeAfterWrite(publicStateChanged, sourceUnavailable);
                }
                catch (HttpOperationException exception)
                    when (exception.Response?.StatusCode == HttpStatusCode.NotFound &&
                          request.AllowedTcpPorts.Count == 0)
                {
                    return new ResourceIngressPolicyApplied();
                }
                catch (HttpOperationException exception)
                    when (exception.Response?.StatusCode == HttpStatusCode.Conflict)
                {
                    V1NetworkPolicy existing;
                    try
                    {
                        existing = await kubernetes.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                            ResourceIngressPolicyManifestBuilder.Name(request),
                            request.KubernetesNamespace,
                            cancellationToken: cancellationToken);
                    }
                    catch (HttpOperationException readException)
                        when (readException.Response?.StatusCode == HttpStatusCode.NotFound)
                    {
                        continue;
                    }

                    if (!OwnsPolicy(existing, request))
                        return new ResourceIngressPolicyOwnershipConflict();
                    if (!CanReplace(existing, current))
                        return new ResourceIngressPolicyStaleRevision();

                    desired.Metadata.ResourceVersion = existing.Metadata!.ResourceVersion;
                    if (!await StillCurrentAsync(request, targetRevision, allowTraffic, cancellationToken))
                        return new ResourceIngressPolicyStaleRevision();
                    try
                    {
                        await kubernetes.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(
                            desired,
                            ResourceIngressPolicyManifestBuilder.Name(request),
                            request.KubernetesNamespace,
                            cancellationToken: cancellationToken);
                        return OutcomeAfterWrite(publicStateChanged, sourceUnavailable);
                    }
                    catch (HttpOperationException replaceException)
                        when (replaceException.Response?.StatusCode == HttpStatusCode.Conflict ||
                              replaceException.Response?.StatusCode == HttpStatusCode.NotFound)
                    {
                        desired.Metadata.ResourceVersion = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentTargetUnavailableException)
        {
            return new ResourceIngressPolicyUnavailable();
        }
        catch (HttpOperationException)
        {
            return new ResourceIngressPolicyUnavailable();
        }
        catch (HttpRequestException)
        {
            return new ResourceIngressPolicyUnavailable();
        }
        catch (OperationCanceledException)
        {
            return new ResourceIngressPolicyUnavailable();
        }

        return new ResourceIngressPolicyUnavailable();
    }

    private static ReconcileResourceIngressPolicyOutcome OutcomeAfterWrite(
        bool publicStateChanged,
        bool sourceUnavailable)
    {
        if (publicStateChanged)
            return new ResourceIngressPolicyStaleRevision();
        if (sourceUnavailable)
            return new ResourceIngressPolicySourceUnavailable();
        return new ResourceIngressPolicyApplied();
    }

    private async Task<bool> StillCurrentAsync(
        ResourceIngressPolicyRequest request,
        long targetRevision,
        bool allowTraffic,
        CancellationToken cancellationToken)
    {
        var deployment = await loadDeployment(request, cancellationToken);
        if (deployment is null ||
            deployment.DesiredGeneration != request.DeploymentDesiredGeneration ||
            deployment.State is DeletedResourceDeploymentState ||
            deployment.DesiredOperation == ResourceDeploymentOperation.Delete &&
            request.AllowedTcpPorts.Count > 0 ||
            deployment.RuntimeCellId != request.RuntimeCellId ||
            deployment.ResolvedPlacement.DeploymentTargetId != request.DeploymentTargetId ||
            deployment.ResolvedPlacement.KubernetesNamespace != request.KubernetesNamespace)
        {
            return false;
        }

        var targetOutcome = await lookupDeploymentTarget(
            request.DeploymentTargetId,
            cancellationToken);
        return targetOutcome is DeploymentTargetLookupDto target &&
               target.WorkloadConfiguration.Revision == targetRevision &&
               (!allowTraffic || target.NetworkPolicy.AllowsWorkloads);
    }

    private static ResourceIngressPolicyTargetSources TargetSources(DeploymentTargetLookupDto target) =>
        new(target.WorkloadConfiguration.NetworkSources.Entries.ToDictionary(
            entry => entry.Key,
            entry => entry.Value switch
            {
                DeploymentTargetPodNetworkSourceDto pod =>
                    (ResourceIngressPolicyNetworkSource)new ResourceIngressPolicyPodSource(
                        pod.Namespace, pod.PodLabels),
                DeploymentTargetCidrNetworkSourceDto cidr =>
                    new ResourceIngressPolicyCidrSource(cidr.Cidr),
                null => null
            },
            StringComparer.Ordinal));

    private static bool OwnsPolicy(V1NetworkPolicy existing, ResourceIngressPolicyRequest request)
    {
        var labels = existing.Metadata?.Labels;
        return labels is not null &&
               ResourceIngressPolicyManifestBuilder.OwnershipLabels(request)
                   .All(expected => labels.TryGetValue(expected.Key, out var value) &&
                                    string.Equals(value, expected.Value, StringComparison.Ordinal));
    }

    private static bool CanReplace(
        V1NetworkPolicy existing,
        ResourceIngressPolicyRequest request)
    {
        var annotations = existing.Metadata?.Annotations;
        if (annotations is null ||
            !annotations.TryGetValue(
                ResourceIngressPolicyManifestBuilder.DeploymentGenerationAnnotation,
                out var generationValue) ||
            !annotations.TryGetValue(
                ResourceIngressPolicyManifestBuilder.TargetConfigurationRevisionAnnotation,
                out var targetRevisionValue) ||
            !long.TryParse(generationValue, NumberStyles.None, CultureInfo.InvariantCulture,
                out var generation) ||
            !long.TryParse(targetRevisionValue, NumberStyles.None, CultureInfo.InvariantCulture,
                out var targetRevision))
        {
            return false;
        }

        return generation <= request.DeploymentDesiredGeneration &&
               targetRevision <= request.TargetWorkloadConfigurationRevision;
    }

    private static void ValidateRequest(
        ResourceIngressPolicyRequest request,
        ResourceIngressPolicyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.AllowedTcpPorts);
        if (request.DeploymentDesiredGeneration < 1 ||
            request.TargetWorkloadConfigurationRevision < 1 ||
            request.AllowedTcpPorts.Any(port => port.Value is < 1 or > 65535))
        {
            throw new ArgumentException("Ingress policy revision or TCP port is invalid.", nameof(request));
        }

        if (request.AllowedTcpPorts.Count > 0 && !definition.AllowsPorts(request.AllowedTcpPorts))
        {
            throw new ArgumentException("Ingress policy ports do not match the provider path.", nameof(request));
        }
    }
}
