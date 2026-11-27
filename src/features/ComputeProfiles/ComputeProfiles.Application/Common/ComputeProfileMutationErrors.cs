// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ComputeProfiles.Application.Common;

public sealed record SelectedComputeProfileTenantsNotFound(
    IReadOnlyList<string> TenantIds
);

public sealed record ComputeProfileTargetMappingsInvalid(
    IReadOnlyDictionary<string, string[]> Errors
);

public sealed record DefaultComputeProfileCannotBeDemoted(
    string ComputeProfileId
);

public sealed record ComputeProfileRevisionConflict(
    string ComputeProfileId
);
