// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.StorageProfiles.Primitives;

public enum StorageMediaType
{
    Unknown = 0,
    HDD = 1,
    SSD = 2,
    NVMe = 3,
    Network = 4,
}

public enum StoragePerformanceTier
{
    Standard = 0,
    Bulk = 1,
    Fast = 2,
    Ultra = 3,
}

public enum StorageProfileTenantAccessMode
{
    AllTenants = 0,
    SelectedTenants = 1,
}
