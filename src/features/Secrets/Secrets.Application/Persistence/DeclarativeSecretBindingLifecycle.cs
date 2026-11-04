using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretBindingProvider
{
    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var secret = await secrets.Find(session,
            SecretFilters.DeclarationBindingId(request.TenantId, request.Resource.ResourceId)).FirstOrDefaultAsync(ct);
        var binding = secret?.Bindings.SingleOrDefault(item => item.Id == request.Resource.ResourceId);
        if (secret is null || binding is null)
            return new GitLifecycleResourceAbsent();
        // An unnamed manual binding has a concrete ID before its first declaration name is adopted.
        if (request.Resource.Address.Kind != Kind ||
            binding.DeclarationName is { } name && name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        if (secret.Management is ProviderManagedSecret owner && owner.BindingId == binding.Id)
            return new DeclarationReadBlocked(new GitLifecycleUnsupported());

        return new GitLifecycleResourceSnapshot(request.Resource, new(SecretPlanningFence.Versions(secret)),
            GitLifecyclePresence.Active, GitLifecycleProtection.ConfigurationOnly,
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

        var secret = await secrets.Find(session, SecretFilters.DeclarationBindingId(request.Authority.TenantId,
            request.Resource.ResourceId)).SingleAsync(ct);
        if (secret.Bindings.Single(item => item.Id == request.Resource.ResourceId).DeclarationName !=
            request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var result = await deletion.ExecuteAsync(new(request.Authority.TenantId, secret.Id, request.Resource.ResourceId,
            request.Expected, request.Authority), session, ct);
        return result switch
        {
            Secret or SecretMissing => new GitLifecycleResourceRemoved(),
            SecretManagedByProvider => new DeclarationReadBlocked(new GitLifecycleUnsupported()),
            SecretGitOwned => new StaleGitExecution()
        };
    }
}
