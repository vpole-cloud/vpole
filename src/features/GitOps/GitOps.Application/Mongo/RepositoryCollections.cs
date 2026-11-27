// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Application.Mongo;

internal static class RepositoryCollections
{
    public const string Previews = "GitOperationPreviews";
    public const string Sources = "GitRepositorySources";
    public const string Synchronization = "GitSynchronizationStates";
    public const string Runs = "GitReconciliationRuns";
    public const string Requests = "GitSourceRequests";
    public const string References = "GitAcceptedReferences";
    public const string HumanLifecycleHistoryIndex = "IX_GitRuns_Tenant_Source_HumanLifecycle_Generation";
    public const string CreationIndex = "UX_GitRepositorySources_Tenant_Creation";
}
