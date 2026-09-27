using System.Globalization;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Kubernetes;

internal sealed class KubernetesResourceIngressPolicyManager(
    LoadIngressPolicyDeployment loadDeployment,
    LookupDeploymentTarget lookupDeploymentTarget,
    EnsureRuntimeCellIngressBaseline ensureRuntimeCellIngressBaseline,
    IKubernetesClientProvider kubernetesClientProvider)
{
    private const int MaximumWriteAttempts = 3;

    public async Task<ReconcileResourceIngressPolicyOutcome> InvokeAsync(
        ResourceIngressPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

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

        var source = ResolveSource(request, target);
        var publicStateChanged = (request.Purpose is
                                     ResourceIngressPolicyPurpose.WebServicePublic or
                                     ResourceIngressPolicyPurpose.WebServiceHttp01) &&
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
                request.TenantId,
                request.RuntimeCellId,
                request.DeploymentTargetId,
                request.KubernetesNamespace,
                cancellationToken);
            if (baseline is RuntimeCellIngressBaselinePlacementMismatch)
                return new ResourceIngressPolicyPlacementMismatch();
            if (baseline is not RuntimeCellIngressBaselineReady)
                return new ResourceIngressPolicyUnavailable();
        }

        var current = request with
        {
            TargetWorkloadConfigurationRevision = targetRevision,
            AllowedTcpPorts = allowTraffic ? request.AllowedTcpPorts : []
        };
        var desired = ResourceIngressPolicyManifestBuilder.Build(
            current,
            allowTraffic ? source : null);

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

    private static DeploymentTargetNetworkSourceDto? ResolveSource(
        ResourceIngressPolicyRequest request,
        DeploymentTargetLookupDto target) => request.Purpose switch
        {
            ResourceIngressPolicyPurpose.WebServicePublic =>
                target.WorkloadConfiguration.NetworkSources.PublicIngress,
            ResourceIngressPolicyPurpose.WebServiceHttp01 =>
                target.WorkloadConfiguration.NetworkSources.PublicIngress,
            ResourceIngressPolicyPurpose.FunctionGateway =>
                target.WorkloadConfiguration.NetworkSources.FunctionGateway,
            ResourceIngressPolicyPurpose.PostgresOperator =>
                target.WorkloadConfiguration.NetworkSources.PostgresOperator,
            ResourceIngressPolicyPurpose.PostgresReplication =>
                ResourceIngressPolicyManifestBuilder.ReplicationSource(request),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

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

    private static void ValidateRequest(ResourceIngressPolicyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.AllowedTcpPorts);
        if (request.DeploymentDesiredGeneration < 1 ||
            request.TargetWorkloadConfigurationRevision < 1 ||
            request.AllowedTcpPorts.Any(port => port is < 1 or > 65535))
        {
            throw new ArgumentException("Ingress policy revision or TCP port is invalid.", nameof(request));
        }

        if (request.AllowedTcpPorts.Count > 0 &&
            (request.Purpose switch
            {
                ResourceIngressPolicyPurpose.WebServiceHttp01 =>
                    !request.AllowedTcpPorts.SequenceEqual([8089]),
                ResourceIngressPolicyPurpose.FunctionGateway =>
                    !request.AllowedTcpPorts.SequenceEqual([8080]),
                ResourceIngressPolicyPurpose.PostgresOperator =>
                    !request.AllowedTcpPorts.Order().SequenceEqual([5432, 8000]),
                ResourceIngressPolicyPurpose.PostgresReplication =>
                    !request.AllowedTcpPorts.SequenceEqual([5432]),
                ResourceIngressPolicyPurpose.WebServicePublic =>
                    request.AllowedTcpPorts.Count != 1,
                _ => true
            }))
        {
            throw new ArgumentException("Ingress policy ports do not match the provider path.", nameof(request));
        }
    }
}
