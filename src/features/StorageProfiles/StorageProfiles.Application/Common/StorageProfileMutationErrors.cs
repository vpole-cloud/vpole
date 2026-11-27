// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.StorageProfiles.Application.Common;

public sealed record StorageProfileTargetMappingsInvalid(
    IReadOnlyDictionary<string, string[]> Errors
);

public sealed record StorageProfileRevisionConflict(
    string StorageProfileId
);

public sealed record StorageProfileTechnicalNameConflict(
    string TechnicalName
);
