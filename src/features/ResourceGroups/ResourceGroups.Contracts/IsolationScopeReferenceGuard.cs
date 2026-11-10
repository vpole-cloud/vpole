using MongoDB.Driver;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record IsolationScopeReferenced(
    string IsolationScopeId
);

public sealed record IsolationScopeUnreferenced;

public union EnsureIsolationScopeUnreferencedOutcome(IsolationScopeUnreferenced, IsolationScopeReferenced);

/// <summary>Feature-owned contributions join the deletion transaction; no foreign collections are exposed.</summary>
public interface IIsolationScopeReferenceGuard
{
    Task<EnsureIsolationScopeUnreferencedOutcome> EnsureUnreferencedAsync(
        EnsureIsolationScopeUnreferencedRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public sealed record EnsureIsolationScopeUnreferencedRequest(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId
);
