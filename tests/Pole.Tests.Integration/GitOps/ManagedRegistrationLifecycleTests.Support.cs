// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected async Task<(RepositorySource Source, GitOperationPreview Preview)>
        PreviewManagedGroupRemovalAsync()
    {
        var owner = (await Services.GetRequiredService<GitRepositorySources>()
            .LoadAsync(Data.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId, [], "remove-managed-group");
        var result = await Services.GetRequiredService<GitLifecyclePreviews>()
            .CreateAsync(
                new CreateLifecyclePreview(
                    new GitSourceIdentity(owner.TenantId, owner.Id),
                    owner.Revision,
                    new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)),
                default);
        Assert.True(
            result is GitOperationPreview,
            result.Value is GitPreviewRejected rejected ? rejected.Failure.GetType().Name : result.Value.ToString());
        return (owner, Assert.IsType<GitOperationPreview>(result.Value));
    }

    private protected async Task InsertUnreviewedUserSecretAsync(
        string tenant,
        string group,
        string scope) => Assert.IsType<Secret>(
        (await Services.GetRequiredService<PersistCreatedSecret>()(
            tenant,
            group,
            scope,
            "manual-child",
            "manual",
            new Dictionary<string, string> { ["key"] = "fixture-value" },
            default)).Value);

    private protected async Task CompleteManagedGroupCleanupAsync(RepositorySource owner, string runId)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var scopes = await DbContext.IsolationScopes.Find(item => item.TenantId == owner.TenantId).ToListAsync();
            foreach (var scope in scopes.Where(item => item.Lifecycle is DeletingIsolationScopeLifecycleState))
            {
                foreach (var cell in await DbContext.RuntimeCells
                             .Find(item => item.IsolationScopeId == scope.Id)
                             .ToListAsync())
                    if (cell.State is DeletingRuntimeCellState)
                        await DbContext.RuntimeCells.ReplaceOneAsync(
                            item => item.Id == cell.Id,
                            cell.MarkDeleted(cell.DesiredGeneration, cell.UpdatedAtUtc.AddSeconds(1)));
                await Services.GetRequiredService<FinalizeResourceGroupDeletion>()(
                    owner.TenantId,
                    scope.ResourceGroupId,
                    scope.Id);
            }

            var run = (await Services.GetRequiredService<GitRepositorySources>()
                .RunAsync(owner.TenantId, owner.Id, runId, default))!;
            if (run.State is GitRunFinished)
                return;
            await ResumeConnectionLifecycleAsync(run);
        }
    }

    private protected sealed class ManagedRegistrationRollback : Exception;
}
