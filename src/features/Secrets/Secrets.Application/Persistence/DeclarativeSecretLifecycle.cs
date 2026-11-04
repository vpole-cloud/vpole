using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Domain;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretProvider
{
    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new GitLifecycleResourceAbsent();
        if (request.Resource.Address.Kind != Kind || current.Name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        if (current.Management is ProviderManagedSecret)
            return new DeclarationReadBlocked(
                new SecretDeclarationRejected(SecretDeclarationError.ProviderManagedSecret));

        return new GitLifecycleResourceSnapshot(request.Resource, new(SecretPlanningFence.Versions(current)),
            GitLifecyclePresence.Active, GitLifecycleProtection.ProtectedData,
            GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource), session, ct);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();
        if (read is DeclarationReadBlocked blocked)
            return blocked;
        if (!SecretLifecycleVersions.Same(((GitLifecycleResourceSnapshot)read.Value).Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var result = await deletion.ExecuteAsync(new(request.Authority.TenantId, request.Resource.ResourceId,
            request.Expected, request.Authority), session, ct);
        return result switch
        {
            Unit or SecretMissing => new GitLifecycleResourceRemoved(),
            SecretBindingsRemain => new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            SecretReferenceRejected => new DeclarationPreconditionsChanged(),
            SecretGitOwned => new StaleGitExecution()
        };
    }
}

internal static class SecretLifecycleVersions
{
    public static bool Same(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}
