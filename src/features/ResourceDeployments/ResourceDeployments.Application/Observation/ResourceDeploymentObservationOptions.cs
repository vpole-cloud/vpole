// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceDeployments.Application.Observation;

public sealed class ResourceDeploymentObservationOptions
{
    public const string SectionName = "ResourceDeployments:Observation";

    public bool Enabled { get; set; } = true;

    public int MaximumSources { get; init; } = 64;

    public int ScanBatchSize { get; init; } = 128;

    public bool Valid => MaximumSources is >= 1 and <= 512 && ScanBatchSize is >= 1 and <= 256;
}
