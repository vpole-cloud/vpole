// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace Pole.Testing;

[AttributeUsage(AttributeTargets.Method)]
public sealed class KubernetesFactAttribute : FactAttribute
{
    public KubernetesFactAttribute(params string[] requiredCapabilities) =>
        Skip = KubernetesLabSelection.GetOptInSkipReason(requiredCapabilities);
}
