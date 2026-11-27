// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestSourceBuildJobDispatcher
{
    public List<string> BuildJobIds { get; } = [];

    public string Invoke(string buildJobId)
    {
        BuildJobIds.Add(buildJobId);
        return $"test-{buildJobId}";
    }
}
