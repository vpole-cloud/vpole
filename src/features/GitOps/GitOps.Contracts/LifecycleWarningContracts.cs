using MongoDB.Driver;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Contracts;

public sealed record ReadLifecycleWarningsRequest(
    string TenantId,
    string ResourceId,
    GitLifecycleAction Action
);

// Optional owner extension. Return stable policy codes only, never configuration or credential values.
public interface IDeclarativeLifecycleWarningProvider
{
    Task<IReadOnlyList<string>> ReadLifecycleWarningsAsync(
        ReadLifecycleWarningsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}
