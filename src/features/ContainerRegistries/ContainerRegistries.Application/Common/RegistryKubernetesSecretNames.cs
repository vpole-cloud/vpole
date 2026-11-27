// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ContainerRegistries.Application.Common;

internal static class RegistryKubernetesSecretNames
{
    public static string ForProject(string registryProjectId) =>
        $"pole-registry-{registryProjectId.ToLowerInvariant()}";
}
