using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;
using KubernetesClient = k8s.Kubernetes;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
[Trait("Layer", "E2E")]
[Trait("Concern", "MultiCluster")]
[Trait("Feature", "WebServices")]
[Trait("Scope", "FullE2E")]
public sealed class MultiClusterWebServiceE2ETests(PoleE2EFixture fixture)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    [KubernetesFact("multi-cluster", "monitoring")]
    public async Task Two_target_reconciliation_isolates_failure_and_retries_only_failed_target()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = timeout.Token;
        var lab = fixture.LabState;
        Assert.Equal("full", lab.Profile);
        var labA = lab.GetRequiredTarget("workload-a");
        var labB = lab.GetRequiredTarget("workload-b");
        using var clientA = new KubernetesClient(labA.CreateConfiguration());
        using var clientB = new KubernetesClient(labB.CreateConfiguration());
        await using var resources = new TestResourceScope("multi-cluster WebService E2E");

        var targetBEnabled = true;
        var serviceName = $"multi-{Guid.NewGuid():N}"[..24];

        var targetA = await resources.CreateAsync(
            $"delete deployment target '{labA.Key}'",
            token => CreateTargetAsync(labA, token),
            (target, token) => DeleteAdminResourceAsync($"/api/v1/platform/deployment-targets/{target.Id}", token),
            cancellationToken);
        var targetB = await resources.CreateAsync(
            $"delete deployment target '{labB.Key}'",
            token => CreateTargetAsync(labB, token),
            (target, token) => DeleteAdminResourceAsync($"/api/v1/platform/deployment-targets/{target.Id}", token),
            cancellationToken);
        Assert.NotEqual(targetA.ClusterIdentity, targetB.ClusterIdentity);

        var computeProfile = await resources.CreateAsync(
            "delete multi-cluster compute profile",
            token => CreateComputeProfileAsync(targetA.Id, targetB.Id, token),
            (profile, token) => DeleteAdminResourceAsync(
                $"/api/v1/platform/compute-profiles/{profile.Id}?revision={profile.Revision}",
                token),
            cancellationToken);
        var namespaceCleanup = resources.CreateConcurrentCleanupGroup("resource-group namespaces");
        var resourceGroupId = await resources.CreateAsync(
            "delete multi-cluster resource group",
            CreateResourceGroupAsync,
            (id, token) => DeleteTenantResourceAsync($"/api/v1/resource-groups/{id}", token),
            cancellationToken);
        var webServiceCleanup = resources.CreateCleanupSequence("multi-cluster WebService");
        var webServiceId = await webServiceCleanup.CreateAsync(
            "delete multi-cluster WebService",
            token => CreateWebServiceAsync(
                serviceName,
                resourceGroupId,
                computeProfile.Id,
                targetA.Id,
                targetB.Id,
                token),
            (id, token) => DeleteTenantResourceAsync($"/api/v1/web-services/{id}", token),
            cancellationToken);
        webServiceCleanup.Defer(
            "wait for multi-cluster WebService deletion",
            token => WaitForNotFoundAsync($"/api/v1/web-services/{webServiceId}", token));

        var applied = await WaitForPlacementsAsync(
            webServiceId,
            placements => placements.Length == 2 && placements.All(IsApplied),
            cancellationToken);
        var cells = await WaitForRuntimeCellsAsync(resourceGroupId, targetA.Id, targetB.Id, cancellationToken);
        var namespaceA = cells[targetA.Id];
        var namespaceB = cells[targetB.Id];
        namespaceCleanup.Defer(
            $"wait for namespace '{namespaceA}' on '{labA.Key}'",
            token => WaitForNamespaceDeletionAsync(clientA, namespaceA, token));
        namespaceCleanup.Defer(
            $"wait for namespace '{namespaceB}' on '{labB.Key}'",
            token => WaitForNamespaceDeletionAsync(clientB, namespaceB, token));
        await AssertNamespaceOwnershipAsync(clientA, namespaceA, cancellationToken);
        await AssertNamespaceOwnershipAsync(clientB, namespaceB, cancellationToken);
        await WaitForDeploymentAvailableAsync(clientA, serviceName, namespaceA, cancellationToken);
        await WaitForDeploymentAvailableAsync(clientB, serviceName, namespaceB, cancellationToken);

        var initialB = await clientB.AppsV1.ReadNamespacedDeploymentAsync(serviceName, namespaceB);
        var initialBRelease = RequiredReleaseId(initialB.Spec.Template.Metadata.Annotations);

        targetB = await UpdateTargetAsync(targetB, enabled: false, cancellationToken);
        targetBEnabled = false;
        resources.Defer("re-enable workload-b deployment target", async token =>
        {
            if (!targetBEnabled)
                targetB = await UpdateTargetAsync(targetB, enabled: true, token);
        });
        await SendAsync(
            fixture.CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"/api/v1/web-services/{webServiceId}/redeploy?expectedRevision=1",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId),
            cancellationToken);

        var partial = await WaitForPlacementsAsync(
            webServiceId,
            placements =>
                placements.Any(item => IsTarget(item, targetA.Id) && IsApplied(item)) &&
                placements.Any(item => IsTarget(item, targetB.Id) &&
                                       RequiredString(item, "status") == "Failed" &&
                                       RequiredString(item, "failureCode") == "DeploymentTargetDisabled"),
            cancellationToken);
        var failedB = partial.Single(item => IsTarget(item, targetB.Id));
        var healthyPlacementBeforeRetry = partial.Single(item => IsTarget(item, targetA.Id));
        var healthyDesiredGeneration = healthyPlacementBeforeRetry.GetProperty("desiredGeneration").GetInt64();
        var healthyObservedGeneration = healthyPlacementBeforeRetry.GetProperty("observedGeneration").GetInt64();
        var healthyBeforeRetry = await clientA.AppsV1.ReadNamespacedDeploymentAsync(serviceName, namespaceA);
        var healthyRelease = RequiredReleaseId(healthyBeforeRetry.Spec.Template.Metadata.Annotations);

        targetB = await UpdateTargetAsync(targetB, enabled: true, cancellationToken);
        targetBEnabled = true;
        using (var retry = fixture.CreateAuthenticatedRequest(
                   HttpMethod.Post,
                   $"/api/v1/web-services/{webServiceId}/deployments/{RequiredString(failedB, "deploymentId")}/retry",
                   fixture.Alpha,
                   fixture.Alpha.DefaultTenantId))
        {
            retry.Content = JsonContent.Create(new
            {
                expectedDeploymentRevision = failedB.GetProperty("deploymentRevision").GetInt64(),
                expectedOperation = "Apply"
            });
            await SendAsync(retry, cancellationToken);
        }

        var retried = await WaitForPlacementsAsync(
            webServiceId,
            placements => placements.Any(item => IsTarget(item, targetB.Id) && IsApplied(item)),
            cancellationToken);
        await WaitForDeploymentAvailableAsync(clientB, serviceName, namespaceB, cancellationToken);
        var healthyAfterRetry = await clientA.AppsV1.ReadNamespacedDeploymentAsync(serviceName, namespaceA);
        var retriedB = await clientB.AppsV1.ReadNamespacedDeploymentAsync(serviceName, namespaceB);

        var healthyPlacementAfterRetry = retried.Single(item => IsTarget(item, targetA.Id));
        Assert.Equal(healthyDesiredGeneration, healthyPlacementAfterRetry.GetProperty("desiredGeneration").GetInt64());
        Assert.Equal(healthyObservedGeneration, healthyPlacementAfterRetry.GetProperty("observedGeneration").GetInt64());
        Assert.Equal(healthyRelease, RequiredReleaseId(healthyAfterRetry.Spec.Template.Metadata.Annotations));
        Assert.NotEqual(initialBRelease, RequiredReleaseId(retriedB.Spec.Template.Metadata.Annotations));
        Assert.Equal(2, applied.Select(item => RequiredString(item, "deploymentTargetId")).Distinct().Count());
    }

    private async Task AssertNamespaceOwnershipAsync(
        IKubernetes client,
        string namespaceName,
        CancellationToken cancellationToken)
    {
        var namespaceResource = await client.CoreV1.ReadNamespaceAsync(
            namespaceName,
            cancellationToken: cancellationToken);
        Assert.Equal(TestRunIdentity.Owner, namespaceResource.Metadata.Labels[TestRunIdentity.OwnerLabel]);
        Assert.Equal(fixture.Identity.WorktreeId, namespaceResource.Metadata.Labels[TestRunIdentity.WorktreeLabel]);
        Assert.Equal(fixture.Identity.RunId, namespaceResource.Metadata.Labels[TestRunIdentity.RunLabel]);
    }

    private async Task<CreatedTarget> CreateTargetAsync(
        KubernetesLabTarget target,
        CancellationToken cancellationToken)
    {
        using var request = fixture.CreatePlatformAdminRequest(
            HttpMethod.Post,
            "/api/v1/platform/deployment-targets/");
        request.Content = JsonContent.Create(new
        {
            displayName = $"E2E {target.Key}",
            enabled = true,
            acceptsWorkloads = true,
            workloadConfiguration = new
            {
                ingress = new
                {
                    ingressClassName = "traefik",
                    clusterIssuerName = "pole-lab",
                    publicBaseDomain = target.PublicBaseDomain
                },
                networkSources = LabNetworkSources()
            },
            connection = new
            {
                kind = "Kubeconfig",
                kubeconfig = await File.ReadAllTextAsync(target.KubeconfigPath, cancellationToken),
                contextName = target.Context
            }
        });
        var json = await SendJsonAsync(request, cancellationToken);
        json = await fixture.AllowTargetWithoutEnforcementAsync(
            RequiredString(json, "id"),
            json.GetProperty("revision").GetInt64(),
            cancellationToken);
        return new CreatedTarget(
            RequiredString(json, "id"),
            RequiredString(json, "clusterIdentity"),
            json.GetProperty("revision").GetInt64(),
            target);
    }

    private async Task<CreatedTarget> UpdateTargetAsync(
        CreatedTarget target,
        bool enabled,
        CancellationToken cancellationToken)
    {
        using var request = fixture.CreatePlatformAdminRequest(
            HttpMethod.Put,
            $"/api/v1/platform/deployment-targets/{target.Id}");
        request.Content = JsonContent.Create(new
        {
            displayName = $"E2E {target.LabTarget.Key}",
            enabled,
            acceptsWorkloads = enabled,
            workloadConfiguration = new
            {
                ingress = new
                {
                    ingressClassName = "traefik",
                    clusterIssuerName = "pole-lab",
                    publicBaseDomain = target.LabTarget.PublicBaseDomain
                },
                networkSources = LabNetworkSources()
            },
            revision = target.Revision
        });
        var json = await SendJsonAsync(request, cancellationToken);
        return target with { Revision = json.GetProperty("revision").GetInt64() };
    }

    private static object LabNetworkSources() => new
    {
        entries = new Dictionary<string, object>
        {
            [DeploymentTargetNetworkSourcesDto.PublicIngressName] = new
            {
                kind = "Pod",
                @namespace = "kube-system",
                podLabels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = "traefik",
                    ["app.kubernetes.io/instance"] = "traefik-kube-system"
                }
            }
        }
    };

    private async Task<CreatedComputeProfile> CreateComputeProfileAsync(
        string targetAId,
        string targetBId,
        CancellationToken cancellationToken)
    {
        using var request = fixture.CreatePlatformAdminRequest(
            HttpMethod.Post,
            "/api/v1/platform/compute-profiles/");
        request.Content = JsonContent.Create(new
        {
            displayName = "E2E multi-target",
            description = "Aspire two-target WebService profile",
            resources = new
            {
                requests = new { cpuMillis = 50, memoryMi = 64 },
                limits = new { cpuMillis = 250, memoryMi = 128 }
            },
            architecture = "Any",
            extendedResources = Array.Empty<object>(),
            allowedResourceTypes = new[] { "pole/web-service" },
            tenantAccess = new { kind = "all-tenants", tenantIds = Array.Empty<string>() },
            targetMappings = new[] { TargetMapping(targetAId), TargetMapping(targetBId) },
            isEnabled = true,
            isDefault = false
        });
        var json = await SendJsonAsync(request, cancellationToken);
        return new CreatedComputeProfile(
            RequiredString(json, "id"),
            json.GetProperty("revision").GetInt64());
    }

    private async Task<string> CreateResourceGroupAsync(CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/v1/resource-groups/",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(new
        {
            name = $"multi-{Guid.NewGuid():N}"[..20],
            description = "Aspire multi-target E2E",
            tags = new[] { "e2e", "multi-target" }
        });
        return RequiredString(await SendJsonAsync(request, cancellationToken), "id");
    }

    private async Task<string> CreateWebServiceAsync(
        string name,
        string resourceGroupId,
        string computeProfileId,
        string targetAId,
        string targetBId,
        CancellationToken cancellationToken)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/v1/web-services/",
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(new
        {
            name,
            resourceGroupId,
            exposure = "Public",
            spec = new
            {
                image = new { server = "docker.io", name = "library/nginx", tag = "1.27.5-alpine" },
                config = new
                {
                    environmentVariables = new Dictionary<string, string>(),
                    portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } }
                },
                autoscaling = (object?)null,
                probes = new { startup = Probe(), liveness = Probe(), readiness = Probe() }
            },
            placement = new
            {
                isolationScopeId = (string?)null,
                targets = new[]
                {
                    new { deploymentTargetId = targetAId, fixedReplicas = 1, computeProfileId },
                    new { deploymentTargetId = targetBId, fixedReplicas = 1, computeProfileId }
                }
            },
            tags = new[] { "e2e", "multi-target" }
        });
        return RequiredString(await SendJsonAsync(request, cancellationToken), "webServiceId");
    }

    private async Task<JsonElement[]> WaitForPlacementsAsync(
        string webServiceId,
        Func<JsonElement[], bool> completed,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/web-services/{webServiceId}/deployments",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            var json = await SendJsonAsync(request, cancellationToken);
            var placements = json.EnumerateArray().Select(item => item.Clone()).ToArray();
            if (completed(placements))
                return placements;
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task<Dictionary<string, string>> WaitForRuntimeCellsAsync(
        string resourceGroupId,
        string targetAId,
        string targetBId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                $"/api/v1/resource-groups/{resourceGroupId}/runtime-cells",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            var json = await SendJsonAsync(request, cancellationToken);
            var cells = json.EnumerateArray()
                .Where(item => RequiredString(item, "state") == "ready")
                .ToDictionary(
                    item => RequiredString(item, "deploymentTargetId"),
                    item => RequiredString(item, "kubernetesNamespace"));
            if (cells.ContainsKey(targetAId) && cells.ContainsKey(targetBId))
                return cells;
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static async Task WaitForDeploymentAvailableAsync(
        KubernetesClient client,
        string name,
        string namespaceName,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(
                    name,
                    namespaceName,
                    cancellationToken: cancellationToken);
                if (deployment.Status.AvailableReplicas >= 1)
                    return;
            }
            catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
            {
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task DeleteTenantResourceAsync(string path, CancellationToken cancellationToken)
    {
        await DeleteWithConflictRetryAsync(
            () => fixture.CreateAuthenticatedRequest(
                HttpMethod.Delete,
                path,
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId),
            cancellationToken);
    }

    private async Task DeleteAdminResourceAsync(string path, CancellationToken cancellationToken)
    {
        await DeleteWithConflictRetryAsync(
            () => fixture.CreatePlatformAdminRequest(HttpMethod.Delete, path),
            cancellationToken);
    }

    private async Task DeleteWithConflictRetryAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = createRequest();
            using var response = await fixture.Api.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return;
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                throw new HttpRequestException(
                    $"Pole API returned {(int)response.StatusCode} for {request.Method} {request.RequestUri}.");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task WaitForNotFoundAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(
                HttpMethod.Get,
                path,
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Cleanup GET '{path}' returned {(int)response.StatusCode}.");
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static async Task WaitForNamespaceDeletionAsync(
        KubernetesClient client,
        string namespaceName,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await client.CoreV1.ReadNamespaceAsync(namespaceName, cancellationToken: cancellationToken);
            }
            catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Pole API returned {(int)response.StatusCode} for {request.Method} {request.RequestUri}.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await fixture.Api.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Pole API returned {(int)response.StatusCode} for {request.Method} {request.RequestUri}.");
    }

    private static bool IsTarget(JsonElement placement, string targetId) =>
        string.Equals(RequiredString(placement, "deploymentTargetId"), targetId, StringComparison.Ordinal);

    private static bool IsApplied(JsonElement placement) =>
        RequiredString(placement, "status") == "Applied" &&
        placement.GetProperty("observedGeneration").GetInt64() >=
        placement.GetProperty("desiredGeneration").GetInt64();

    private static string RequiredReleaseId(IDictionary<string, string>? annotations) =>
        annotations is not null && annotations.TryGetValue("pole.server/releaseId", out var releaseId)
            ? releaseId
            : throw new InvalidOperationException("WebService Deployment has no Pole release annotation.");

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidOperationException($"Pole response is missing non-empty '{name}'.");

    private static object TargetMapping(string deploymentTargetId) => new
    {
        deploymentTargetId,
        nodeSelector = Array.Empty<object>(),
        requiredNodeAffinity = Array.Empty<object>(),
        preferredNodeAffinity = Array.Empty<object>(),
        tolerations = Array.Empty<object>(),
        runtimeClassName = (string?)null
    };

    private static object Probe() => new
    {
        path = "/",
        port = "80",
        initialDelaySeconds = 0,
        periodSeconds = 5,
        timeoutSeconds = 3,
        failureThreshold = 3,
        successThreshold = 1
    };

    private sealed record CreatedTarget(
        string Id,
        string ClusterIdentity,
        long Revision,
        KubernetesLabTarget LabTarget);

    private sealed record CreatedComputeProfile(
        string Id,
        long Revision);
}
