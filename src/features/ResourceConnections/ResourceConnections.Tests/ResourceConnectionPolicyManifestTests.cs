// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using Pole.Features.ResourceConnections.Application.Kubernetes;
using Pole.Features.ResourceConnections.Domain;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.ResourceConnections.Tests;

public sealed class ResourceConnectionPolicyManifestTests
{
    [Fact]
    public void Build_AllowsOnlyTheResolvedSourceDeploymentToTheResolvedDestinationPort()
    {
        var snapshot = ConnectionTargetSnapshot.Create(
            "target-id",
            "destination-deployment",
            "destination-cell",
            "destination-namespace",
            "cache-svc",
            80,
            8080,
            [new ConnectionSourcePlacement("source-deployment", "source-cell", "source-namespace")]);
        var connection = ResourceConnectionFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            "tenant-id",
            "orders-cache",
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            "http",
            [snapshot]);

        var policy = ResourceConnectionPolicyManifestBuilder.Build(connection, snapshot);

        Assert.Equal($"pole-connection-{connection.Id}", policy.Metadata.Name);
        Assert.Equal("destination-namespace", policy.Metadata.NamespaceProperty);
        Assert.Equal("tenant-id", policy.Metadata.Labels[ResourceConnectionPolicyManifestBuilder.TenantLabel]);
        Assert.Equal(
            "destination-cell",
            policy.Spec.PodSelector.MatchLabels[ResourceConnectionPolicyManifestBuilder.RuntimeCellLabel]);
        Assert.Equal(
            "destination-deployment",
            policy.Spec.PodSelector.MatchLabels[ResourceConnectionPolicyManifestBuilder.DeploymentLabel]);
        Assert.Equal(["Ingress"], policy.Spec.PolicyTypes);
        var ingress = Assert.Single(policy.Spec.Ingress);
        var port = Assert.Single(ingress.Ports);
        Assert.Equal("TCP", port.Protocol);
        Assert.Equal("8080", port.Port.Value);
        var peer = Assert.Single(ingress.FromProperty);
        Assert.Equal(
            "tenant-id",
            peer.NamespaceSelector.MatchLabels[ResourceConnectionPolicyManifestBuilder.TenantLabel]);
        Assert.Equal(
            "source-cell",
            peer.NamespaceSelector.MatchLabels[ResourceConnectionPolicyManifestBuilder.RuntimeCellLabel]);
        Assert.Equal("tenant-id", peer.PodSelector.MatchLabels[ResourceConnectionPolicyManifestBuilder.TenantLabel]);
        Assert.Equal(
            "source-deployment",
            peer.PodSelector.MatchLabels[ResourceConnectionPolicyManifestBuilder.DeploymentLabel]);
        Assert.True(
            ResourceConnectionPolicyManifestBuilder.IsOwned(
                policy,
                connection.TenantId,
                connection.Id,
                snapshot.PolicyLocation));
        Assert.False(
            ResourceConnectionPolicyManifestBuilder.IsOwned(
                policy,
                "other-tenant",
                connection.Id,
                snapshot.PolicyLocation));
    }
}
