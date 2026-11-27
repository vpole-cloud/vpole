// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Contracts;

// Version tokens and preview proofs compare these keys by ordinal string equality.
public static class DeclarationVersionKeys
{
    public static string Resource(string kind, string resourceId) => $"{kind}/{resourceId}";

    public static string PlanningFence(string kind, string resourceId) => $"{Resource(kind, resourceId)}/PlanningFence";
}
