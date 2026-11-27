// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Application.Mongo;

internal static class GitOpsCollections
{
    public const string Inventory = "ManagedDeclarations";

    public const string Plans = "GitOperationPlans";

    public const string CleanupProofs = "GitLifecycleCleanupProofs";

    public const string Receipts = "GitOperationReceipts";
}
