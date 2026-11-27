// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Lifecycle;

public sealed class GitLifecycleCleanupProofs(
    IMongoCollection<GitLifecycleCleanupProof> proofs,
    IMongoCollection<GitOperationReceipt> receipts,
    IMongoCollection<GitReconciliationRun> runs,
    IMongoCollection<ManagedDeclaration> inventory,
    IServiceProvider services
) : IGitLifecycleCleanupProofRecorder
{
    public async Task RecordAsync(
        RecordGitLifecycleCleanupRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        GitTransactionGuard.Require(session);
        var identity = request.Resource switch
        {
            GitRegisteredCleanupResource resource => await ResolveRegisteredAsync(
                request.TenantId,
                resource,
                session,
                cancellationToken),
            GitDeclaredCleanupResource resource => new CleanupIdentity(resource.Kind, resource.ResourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        if (identity is null)
            return;

        var (kind, resourceId) = identity;
        var owner = await inventory
            .Find(session, ManagedDeclarationFilters.Resource(request.TenantId, kind, resourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is null)
            return;

        var candidates = await receipts.Find(
                session,
                GitLifecycleCleanupFilters.BeginReceipts(request.TenantId, owner.SourceId, kind, resourceId))
            .ToListAsync(cancellationToken);
        foreach (var receipt in candidates)
        {
            var run = await runs.Find(
                    session,
                    GitLifecycleCleanupFilters.Run(request.TenantId, owner.SourceId, receipt.RunId))
                .FirstOrDefaultAsync(cancellationToken);
            if (run?.Input is not GitInputLifecycle lifecycle || run.Stamp.Fence != receipt.Fence ||
                !lifecycle.Plan.Operations.Any(item => item.Id == receipt.Lifecycle!.ActionId &&
                    item.Resource == receipt.Lifecycle.Subject &&
                    item.Action is GitLifecycleAction.Remove or GitLifecycleAction.Replace))
                continue;
            var proof = GitLifecycleCleanupProofFactory.Create(
                receipt,
                request.CleanupId,
                request.Transitions.Select(item =>
                    new GitCleanupDocumentTransition(item.DocumentId, item.Before, item.After)));
            var existing = await proofs.Find(session, GitLifecycleCleanupFilters.Proof(proof.Id))
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is null)
                await proofs.InsertOneAsync(session, proof, cancellationToken: cancellationToken);
            else if (existing.BeginReceiptId != proof.BeginReceiptId || existing.Subject != proof.Subject ||
                     !existing.Transitions.SequenceEqual(proof.Transitions))
                throw new InvalidOperationException("A recorded cleanup proof cannot change its causal transition.");
        }
    }

    private async Task<CleanupIdentity?> ResolveRegisteredAsync(
        string tenant,
        GitRegisteredCleanupResource resource,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var providers = services.GetServices<IDeclarativeResourceProvider>()
            .Where(item => item is IDeclarativeRegisteredResourceProvider)
            .ToDictionary(
                item => ((IDeclarativeRegisteredResourceProvider)item).ResourceType.Value,
                StringComparer.Ordinal);
        if (!providers.TryGetValue(resource.ResourceType.Value, out var provider))
            return null;

        if (provider is not IDeclarativeRegistrationOwnershipProvider ownership)
            return new CleanupIdentity(provider.Kind, resource.ResourceId);

        var read = await ownership.ReadRegistrationOwnershipAsync(
            new ReadRegisteredResourceOwnershipRequest(tenant, resource.ResourceId),
            session,
            ct);
        return read switch
        {
            IndependentRegisteredResourceOwnership => new CleanupIdentity(provider.Kind, resource.ResourceId),
            ManagedRegisteredResourceOwnership managed => providers.TryGetValue(managed.OwnerType.Value, out var owner)
                ? new CleanupIdentity(owner.Kind, managed.OwnerId)
                : null,
            DeclarationReadBlocked => null,
        };
    }

    public async Task<ImmutableArray<GitResourceVersion>> AdvanceAsync(
        GitReconciliationRun run,
        GitLifecycleExecutionPlan plan,
        IReadOnlyList<GitOperationReceipt> stored,
        IReadOnlyList<GitPreviewOperation> operations,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var begin = stored.Where(item => item.Blocker is null && item.Fence == run.Stamp.Fence &&
                item.Lifecycle?.Step == GitLifecycleStep.BeginRemoval)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (begin.Count == 0)
            return [];
        var found = await proofs.Find(session, GitLifecycleCleanupFilters.Proofs(run.Stamp, run.Id)).ToListAsync(ct);
        var valid = found.Where(item => begin.TryGetValue(item.BeginReceiptId, out var receipt) &&
                receipt.Lifecycle!.ActionId == item.ActionId && receipt.Lifecycle.Subject == item.Subject)
            .ToArray();
        var effectVersions = stored.Where(item => item.Blocker is null && item.Lifecycle is not null)
            .Select(item => item.Lifecycle!)
            .OrderBy(item => item.Sequence)
            .SelectMany(item => item.RelatedVersions.Concat(item.Postcondition?.Versions ?? []))
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var references = plan.References
            .SelectMany(item => item.Versions)
            .Select(item => effectVersions.GetValueOrDefault(item.DocumentId) ?? item);
        return GitLifecycleCleanupAdvancement.Advance(
            operations.SelectMany(item => item.Expected?.Versions ?? []).Concat(references),
            valid);
    }

    public static GitResourcePreconditions? Advance(
        GitResourcePreconditions? expected,
        IReadOnlyList<GitResourceVersion> versions) => expected is null
        ? null
        : expected with
        {
            Versions = expected.Versions
                .Select(item => versions.SingleOrDefault(version => version.DocumentId == item.DocumentId) ?? item)
                .ToImmutableArray(),
        };

    private sealed record CleanupIdentity(
        string Kind,
        string ResourceId
    );
}
