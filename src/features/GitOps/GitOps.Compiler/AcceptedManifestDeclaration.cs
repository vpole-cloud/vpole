// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Compiler;

public sealed record AcceptedManifestDeclaration(
    string Kind,
    string Name,
    string SpecJson
);
