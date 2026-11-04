using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Tests.Integration.Infrastructure;

namespace Pole.Tests.Integration.WebServices;

// Tests isolate the owner transaction boundary; the GitOps suite exercises real preview/lease admission.
internal sealed class LifecycleOwnerAuthority : IGitOpsMutations, IGitLifecyclePlanGuard
{
    public GitLifecycleAuthority Authority { get; } = new(TestData.TenantId, "source", "run", 1, 1, 1,
        "worker", 1, "preview", "action");
    public IReadOnlyList<GitLifecycleApprovedResource> Resources { get; set; } = [];
    public Dictionary<ResourceAddress, string> References { get; } = [];
    public GitLifecycleMutationEffect? Effect { get; private set; }
    public HashSet<(string Kind, string Id)> Managed { get; } = [];

    public Task<ReadLifecycleApprovedScopeOutcome> ReadScopeAsync(GitLifecycleAuthority authority,
        IClientSessionHandle session, CancellationToken ct) => Task.FromResult<ReadLifecycleApprovedScopeOutcome>(
        authority == Authority ? new GitLifecycleApprovedScope(Resources) : new StaleGitExecution());

    public Task<bool> AuthorizeAsync(AuthorizeGitResourceMutationRequest request,
        IClientSessionHandle session, CancellationToken ct)
    {
        Effect = request.LifecycleEffect;
        return Task.FromResult(request.Lifecycle is null
            ? request.Context is null && !Managed.Contains((request.Kind, request.ResourceId))
            : request.Lifecycle == Authority &&
            request.LifecycleEffect is GitLifecycleMutationEffect.Adopt or GitLifecycleMutationEffect.Remove);
    }

    public Task<string?> ResolveAcceptedAsync(ResolveAcceptedGitReferenceRequest request,
        IClientSessionHandle session, CancellationToken ct) =>
        Task.FromResult(References.GetValueOrDefault(request.Address));

    public Task<IReadOnlyDictionary<string, GitResourceStatus>> StatusesAsync(LoadGitResourceStatusesRequest request,
        CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, GitResourceStatus>>(
        new Dictionary<string, GitResourceStatus>());

    public Task<GitResourceStatus?> StatusAsync(LoadGitResourceStatusRequest request, CancellationToken ct) =>
        Task.FromResult<GitResourceStatus?>(null);

    public Task<GitOperationResult> ExecuteAsync(GitOperation operation,
        Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<GitOperationResult?> BeginAsync(GitOperation operation, IClientSessionHandle session,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<GitOperationResult> CompleteAsync(CompleteGitOperationRequest request, IClientSessionHandle session,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> ResolveAsync(ResolveGitResourceRequest request, IClientSessionHandle session,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<PinGitReferenceOutcome> PinReferenceAsync(PinGitReferenceRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
}
