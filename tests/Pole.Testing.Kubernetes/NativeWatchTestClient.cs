// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;

namespace Pole.Testing.Kubernetes;

public static class NativeWatchTestClient
{
    public static k8s.Kubernetes Create(DelegatingHandler terminal) => new(
        new KubernetesClientConfiguration { Host = "http://native-watch.test" },
        new KubernetesWatchHeaders(),
        terminal);

    public static bool IsWatch(Uri uri) => uri.Query.Contains("watch=true", StringComparison.OrdinalIgnoreCase);

    public static string DeploymentList(
        string version,
        string? continuation = null,
        params V1Deployment[] items) => KubernetesJson.Serialize(
        new V1DeploymentList
        {
            ApiVersion = "apps/v1",
            Kind = "DeploymentList",
            Metadata = new V1ListMeta { ResourceVersion = version, ContinueProperty = continuation },
            Items = items,
        });
}
