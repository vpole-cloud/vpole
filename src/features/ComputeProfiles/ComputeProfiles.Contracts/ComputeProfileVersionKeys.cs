// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ComputeProfiles.Contracts;

public static class ComputeProfileVersionKeys
{
    public static string Policy(string profileId) => $"ComputeProfile/{profileId}/Policy";

    public static string Revision(string profileId, long revision) => $"ComputeProfile/{profileId}/Revision/{revision}";
}
