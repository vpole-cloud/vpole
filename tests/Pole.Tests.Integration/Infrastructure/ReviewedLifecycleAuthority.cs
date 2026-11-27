// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Tests.Integration.Infrastructure;

// Owner tests isolate the shared-session boundary; GitOps tests verify real preview admission and receipts.
internal sealed class ReviewedLifecycleAuthority(
    IMongoClient client,
    string tenantId
) : IGitOpsMutations
{
    private readonly Dictionary<(string Consumer, ResourceAddress Address), string> references = [];

    public GitLifecycleAuthority Authority { get; } = new(
        tenantId,
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        1,
        1,
        1,
        "worker",
        1,
        "preview",
        "reviewed",
        new string('a', 40));

    public GitLifecycleAuthority? Observed { get; private set; }
    public HashSet<string> GitOwnedResources { get; } = [];

    public Task<bool> AuthorizeAsync(
        AuthorizeGitResourceMutationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        Observed = request.Lifecycle;
        return Task.FromResult(
            request.Lifecycle is null
                ? !GitOwnedResources.Contains(request.ResourceId)
                : request.Lifecycle == Authority && request.LifecycleEffect == GitLifecycleMutationEffect.Remove);
    }

    public async Task<GitOperationResult> ExecuteAsync(
        GitOperation operation,
        Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation,
        CancellationToken ct)
    {
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(
            async (current, token) =>
            {
                var result = await mutation(current, token);
                if (!result.Applied)
                    await current.AbortTransactionAsync(token);
                return result;
            },
            cancellationToken: ct);
    }

    public Task<string?> ResolveAcceptedAsync(
        ResolveAcceptedGitReferenceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        Task.FromResult(references.GetValueOrDefault((request.ConsumerResourceId, request.Address)));

    public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct)
    {
        references.TryAdd((request.ConsumerResourceId, request.Target.Address), request.Target.ResourceId);
        return Task.FromResult<PinGitReferenceOutcome>(
            new GitReferencePinned(references[(request.ConsumerResourceId, request.Target.Address)]));
    }

    public Task<GitOperationResult?> BeginAsync(
        GitOperation operation,
        IClientSessionHandle session,
        CancellationToken ct) => throw new NotSupportedException();

    public Task<GitOperationResult> CompleteAsync(
        CompleteGitOperationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> ResolveAsync(
        ResolveGitResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
        LoadGitResourceStatusesRequest request,
        CancellationToken ct) => throw new NotSupportedException();

    public Task<GitResourceStatus?> StatusAsync(LoadGitResourceStatusRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
}
