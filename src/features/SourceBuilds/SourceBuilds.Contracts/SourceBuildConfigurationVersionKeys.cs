// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.SourceBuilds.Contracts;

public static class SourceBuildConfigurationVersionKeys
{
    public static string Revision(string id) => $"SourceBuildConfiguration/{id}";
}
