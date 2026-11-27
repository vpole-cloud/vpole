// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SourceBuildFactAttribute : FactAttribute
{
    public SourceBuildFactAttribute()
    {
        if (KubernetesLabSelection.GetOptInSkipReason("harbor", "buildkit", "monitoring") is { } labSkip)
            Skip = labSkip;
    }
}
