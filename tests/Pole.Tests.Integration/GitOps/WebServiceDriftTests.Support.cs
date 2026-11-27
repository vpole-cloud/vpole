// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected sealed class BeforeNoOp(
        IGitOpsMutations inner,
        Func<GitOperation, CancellationToken, Task> action
    ) : DelegatingGitOpsMutations(inner)
    {
        public override async Task<GitOperationResult> ExecuteAsync(
            GitOperation operation,
            Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation,
            CancellationToken ct)
        {
            if (operation.Plan.Decision is NoOpDeclarationPlan)
                await action(operation, ct);
            return await base.ExecuteAsync(operation, mutation, ct);
        }
    }
}
