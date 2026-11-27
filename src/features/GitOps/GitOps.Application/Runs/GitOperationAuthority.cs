// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Runs;

internal static class GitOperationAuthority
{
    public static bool Matches(GitReconciliationRun? run, GitOperationInput operation) =>
        run?.AcceptedSnapshot is { } accepted && run.Commit == operation.Context.Authority.Commit &&
        accepted.Resources.Any(item => item.Kind == operation.Declaration.Address.Kind &&
            item.Name == operation.Declaration.Address.Name && item.ResourceId == operation.ResourceId &&
            item.Hash == operation.Declaration.Hash) && accepted.Operations.Any(item =>
            item.Kind == operation.Declaration.Address.Kind && item.Name == operation.Declaration.Address.Name &&
            item.ResourceId == operation.ResourceId && item.Phase == operation.Phase);
}
