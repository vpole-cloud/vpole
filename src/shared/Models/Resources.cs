// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Shared.Models;

public record Resources(
    Resource Requests,
    Resource Limits
);

public record Resource(
    int CpuCores,
    int MemoryMi
);
