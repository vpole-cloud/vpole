// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Kubernetes;

internal static class WebServiceIngressPolicyDefinitions
{
    public const string PublicPurpose = "web-public";
    public const string Http01Purpose = "web-http01";

    public static ResourceIngressPolicyDefinition Public { get; } = new(
        WebServiceResourceDeployment.ResourceType,
        PublicPurpose,
        RequiresIngressPlacement: true,
        ports => ports.Count == 1,
        WorkloadLabels,
        (_, sources) => sources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName));

    public static ResourceIngressPolicyDefinition Http01 { get; } = new(
        WebServiceResourceDeployment.ResourceType,
        Http01Purpose,
        RequiresIngressPlacement: true,
        ports => ports.SequenceEqual([NetworkPort.Create(8089)]),
        _ => ImmutableDictionary<string, string>.Empty.Add("acme.cert-manager.io/http01-solver", "true"),
        (_, sources) => sources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName));

    private static ImmutableDictionary<string, string> WorkloadLabels(ResourceIngressPolicyRequest request) =>
        ImmutableDictionary<string, string>.Empty
            .Add("pole.io/tenant-id", request.TenantId)
            .Add("pole.io/runtime-cell-id", request.RuntimeCellId)
            .Add("pole.io/resource-deployment-id", request.ResourceDeploymentId);
}
