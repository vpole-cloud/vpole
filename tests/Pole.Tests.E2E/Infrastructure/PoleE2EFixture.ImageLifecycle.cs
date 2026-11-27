// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s;
using Pole.Testing;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    public async Task WaitForOwnedImageServiceDeletionAsync(
        string groupId,
        string serviceId,
        string expectedDeploymentUid,
        CancellationToken ct)
    {
        using var timing = _timings.Measure("fixture.e2e.image.native-deletion");
        var recorded = _kubernetesNamespaces.TryGetValue(groupId, out var owned)
            ? owned
            : throw new InvalidOperationException("No owned namespace was recorded for the image service.");
        var selector = $"pole.io/resource-id={serviceId}";
        await TestWait.UntilAsync(
            async probe =>
            {
                var currentNamespace = await _kubernetes.CoreV1.ReadNamespaceAsync(
                    recorded.Name,
                    cancellationToken: probe);
                VerifyOwnedNamespace(currentNamespace, recorded.Uid);
                var deployments = await _kubernetes.AppsV1.ListNamespacedDeploymentAsync(
                    recorded.Name,
                    labelSelector: selector,
                    cancellationToken: probe);
                if (deployments.Items.Any(item => item.Metadata.Uid != expectedDeploymentUid))
                    throw new InvalidOperationException(
                        "The image service acquired an unexpected native deployment identity during deletion.");
                var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
                    recorded.Name,
                    labelSelector: selector,
                    cancellationToken: probe);
                var services = await _kubernetes.CoreV1.ListNamespacedServiceAsync(
                    recorded.Name,
                    labelSelector: selector,
                    cancellationToken: probe);
                var configMaps = await _kubernetes.CoreV1.ListNamespacedConfigMapAsync(
                    recorded.Name,
                    labelSelector: selector,
                    cancellationToken: probe);
                var ingresses = await _kubernetes.NetworkingV1.ListNamespacedIngressAsync(
                    recorded.Name,
                    labelSelector: selector,
                    cancellationToken: probe);
                var autoscalers = await _kubernetes.AutoscalingV2.ListNamespacedHorizontalPodAutoscalerAsync(
                    recorded.Name,
                    labelSelector: selector,
                    cancellationToken: probe);
                return autoscalers.Items.Count == 0 && deployments.Items.Count == 0 && pods.Items.Count == 0 &&
                    services.Items.Count == 0 && configMaps.Items.Count == 0 && ingresses.Items.Count == 0;
            },
            TimeSpan.FromMinutes(3),
            "The deleted image service still has owned native resources.",
            ct,
            TimeSpan.FromMilliseconds(500));
    }
}
