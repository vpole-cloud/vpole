// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Domain;

public sealed record AcceptedDeclaration(
    string Kind,
    string Name,
    string Hash,
    string SpecJson,
    string ResourceId
);
