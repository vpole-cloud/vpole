// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.ArchitectureAnalyzers;

public static class ArchitectureProjectRoles
{
    public const string Production = "Production";
    public const string Migration = "Migration";
    public const string TestSupport = "TestSupport";

    public static bool ExemptsMongoRules(string? role, bool isTestProject) =>
        isTestProject || role is Migration or TestSupport;
}
