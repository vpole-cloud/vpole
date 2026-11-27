// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceConnections.Application.Kubernetes;

internal static class ResourceConnectionServiceDns
{
    public static string Build(string serviceName, string namespaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        return $"{serviceName}.{namespaceName}.svc.cluster.local";
    }
}
