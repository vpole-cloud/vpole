using MongoDB.Driver;
using Pole.ResultType;
namespace Pole.Features.ResourceGroups.Contracts;

public sealed record IsolationScopeReferenced(string IsolationScopeId);

/// <summary>Feature-owned contributions join the deletion transaction; no foreign collections are exposed.</summary>
public interface IIsolationScopeReferenceGuard
{
    Task<Result<Unit, IsolationScopeReferenced>> EnsureUnreferencedAsync(
        string tenantId, string resourceGroupId, string isolationScopeId,
        IClientSessionHandle session, CancellationToken cancellationToken = default);
}
