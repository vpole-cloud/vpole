// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.StorageProfiles.Contracts;

public static class StorageProfileVersionKeys
{
    public static string Policy(string profileId) => $"StorageProfile/{profileId}/Policy";
}
