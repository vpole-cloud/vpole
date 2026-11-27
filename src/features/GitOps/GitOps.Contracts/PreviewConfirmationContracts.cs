// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Contracts;

// Refer to server-owned actions. No client-supplied operations or authorization claims.
public sealed record PreviewConfirmationReference(
    string PreviewId,
    long ExpectedRevision
);
