// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Compiler;

public sealed record CompiledSnapshot(
    IReadOnlyList<CompiledDeclaration> Declarations,
    IReadOnlyList<DeclarationDiagnostic> Diagnostics
)
{
    public bool Valid => Diagnostics.Count == 0;
}
