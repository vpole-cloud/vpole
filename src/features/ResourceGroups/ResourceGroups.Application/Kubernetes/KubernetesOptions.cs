// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceGroups.Application.Kubernetes;

public sealed class KubernetesOptions
{
    public const string SectionName = "kubernetes";
    public required string NamespaceNamePrefix { get; set; }

    public Dictionary<string, string> AdditionalNamespaceLabels { get; set; } = new(StringComparer.Ordinal);
}
