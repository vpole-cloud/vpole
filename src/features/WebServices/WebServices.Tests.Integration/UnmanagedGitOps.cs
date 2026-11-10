using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.WebServices.Tests.Integration;

internal sealed class UnmanagedGitOps : IGitOpsMutations
{
    public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(
        LoadGitResourceStatusesRequest request,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, GitResourceStatus>>(new Dictionary<string, GitResourceStatus>());

    public Task<GitResourceStatus?> StatusAsync(LoadGitResourceStatusRequest request, CancellationToken ct) =>
        Task.FromResult<GitResourceStatus?>(null);

    public Task<bool> AuthorizeAsync(
        AuthorizeGitResourceMutationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => Task.FromResult(true);

    public Task<GitOperationResult> ExecuteAsync(
        GitOperation operation,
        Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation,
        CancellationToken ct) => throw new InvalidOperationException("Unexpected GitOps operation.");

    public Task<GitOperationResult?> BeginAsync(
        GitOperation operation,
        IClientSessionHandle session,
        CancellationToken ct) => throw new InvalidOperationException("Unexpected GitOps operation.");

    public Task<GitOperationResult> CompleteAsync(
        CompleteGitOperationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => throw new InvalidOperationException("Unexpected GitOps operation.");

    public Task<string?> ResolveAsync(
        ResolveGitResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => throw new InvalidOperationException("Unexpected GitOps reference resolution.");

    public Task<string?> ResolveAcceptedAsync(
        ResolveAcceptedGitReferenceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) => throw new InvalidOperationException("Unexpected GitOps reference resolution.");

    public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("Unexpected GitOps reference pinning.");
}
