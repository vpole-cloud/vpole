// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Compiler;

public static class SnapshotSchema
{
    public const string ApiVersion = "pole.io/v1alpha1";

    public const int MaximumFiles = SnapshotInputLimits.MaximumFiles;

    public const int MaximumDocumentsPerFile = 256;

    public const int MaximumDeclarations = 256;

    public const int MaximumContentBytes = SnapshotInputLimits.MaximumContentBytes;

    public const int MaximumStoredSpecBytes = 6 * MaximumContentBytes;

    public const int MaximumYamlDepth = 24;
}
