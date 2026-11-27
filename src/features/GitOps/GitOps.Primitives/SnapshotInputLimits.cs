// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Primitives;

public static class SnapshotInputLimits
{
    public const int MaximumFiles = 256;
    public const int MaximumContentBytes = 1024 * 1024;
}
