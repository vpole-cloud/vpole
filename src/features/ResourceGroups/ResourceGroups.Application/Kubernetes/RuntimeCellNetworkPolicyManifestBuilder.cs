// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s.Models;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

/// <summary>The first policy installed in every runtime cell, before the cell becomes ready.</summary>
public static class RuntimeCellNetworkPolicyManifestBuilder
{
    public const string Name = "pole-default-deny-ingress";

    public static V1NetworkPolicy Build(RuntimeCell runtimeCell) => new()
    {
        ApiVersion = "networking.k8s.io/v1",
        Kind = "NetworkPolicy",
        Metadata = new V1ObjectMeta
        {
            Name = Name,
            NamespaceProperty = runtimeCell.KubernetesNamespace,
            Labels = RuntimeCellNamespaceManifestBuilder.ExpectedOwnershipLabels(runtimeCell),
        },
        Spec = new V1NetworkPolicySpec
        {
            PodSelector = new V1LabelSelector(),
            PolicyTypes = ["Ingress"],
            Ingress = [],
        },
    };
}
