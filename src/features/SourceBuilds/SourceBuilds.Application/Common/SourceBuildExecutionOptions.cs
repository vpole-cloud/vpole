// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.SourceBuilds.Application.Common;

public sealed class SourceBuildExecutionOptions
{
    public const string SectionName = "SourceBuildExecution";

    public string WorkspaceRoot { get; init; } = Path.Combine(Path.GetTempPath(), "pole-source-builds");

    public long MaxArchiveBytes { get; init; } = 100L * 1024 * 1024;
    public long MaxExtractedBytes { get; init; } = 500L * 1024 * 1024;
    public int MaxExtractedFiles { get; init; } = 50_000;
}

public sealed class BuildKitOptions
{
    public const string SectionName = "BuildKit";

    public bool Enabled { get; init; }
    public string Address { get; init; } = "tcp://localhost:1234";
    public string BuildctlPath { get; init; } = "buildctl";
    public int TimeoutMinutes { get; init; } = 30;

    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(BuildctlPath) &&
        TimeoutMinutes is > 0 and <= 180;
}
