// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel.DataAnnotations;

namespace Pole.Features.GitProviders.Application.Snapshots;

public sealed class RepositoryArchiveOptions
{
    public const string SectionName = "GitHubRepositoryArchive";

    [Range(1, long.MaxValue)] public long MaxCompressedBytes { get; init; } = 100L * 1024 * 1024;

    [Range(1, long.MaxValue)] public long MaxDecompressedBytes { get; init; } = 500L * 1024 * 1024;

    [Range(1, int.MaxValue)] public int MaxEntries { get; init; } = 50_000;
}
