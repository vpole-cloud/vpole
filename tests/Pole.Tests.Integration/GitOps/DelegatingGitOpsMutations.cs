using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;

namespace Pole.Tests.Integration.GitOps;

internal class DelegatingGitOpsMutations(IGitOpsMutations inner) : IGitOpsMutations
{
    public virtual Task<GitOperationResult> ExecuteAsync(GitOperation operation,
        Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation, CancellationToken ct) =>
        inner.ExecuteAsync(operation, mutation, ct);

    public Task<GitOperationResult?> BeginAsync(GitOperation operation, IClientSessionHandle session,
        CancellationToken ct) => inner.BeginAsync(operation, session, ct);

    public Task<GitOperationResult> CompleteAsync(CompleteGitOperationRequest request, IClientSessionHandle session,
        CancellationToken ct) => inner.CompleteAsync(request, session, ct);

    public virtual Task<bool> AuthorizeAsync(AuthorizeGitResourceMutationRequest request, IClientSessionHandle session,
        CancellationToken ct) => inner.AuthorizeAsync(request, session, ct);

    public Task<string?> ResolveAsync(ResolveGitResourceRequest request, IClientSessionHandle session,
        CancellationToken ct) => inner.ResolveAsync(request, session, ct);

    public Task<string?> ResolveAcceptedAsync(ResolveAcceptedGitReferenceRequest request, IClientSessionHandle session,
        CancellationToken ct) => inner.ResolveAcceptedAsync(request, session, ct);

    public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct) =>
        inner.PinReferenceAsync(request, ct);

    public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(LoadGitResourceStatusesRequest request,
        CancellationToken ct) => inner.StatusesAsync(request, ct);

    public Task<GitResourceStatus?> StatusAsync(LoadGitResourceStatusRequest request, CancellationToken ct) =>
        inner.StatusAsync(request, ct);
}
