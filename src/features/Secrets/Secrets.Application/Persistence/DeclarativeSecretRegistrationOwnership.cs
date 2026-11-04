using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretProvider
{
    public async Task<ReadRegisteredResourceOwnershipOutcome> ReadRegistrationOwnershipAsync(
        ReadRegisteredResourceOwnershipRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        var version = new DeclarationVersionToken(SecretPlanningFence.Versions(current));
        if (current.Management is UnmanagedSecret)
            return new IndependentRegisteredResourceOwnership(version);
        if (current.Management is not ProviderManagedSecret owner || current.Bindings.Length != 1 ||
            string.IsNullOrWhiteSpace(owner.ResourceId))
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        var binding = current.Bindings[0];
        if (binding.Id != owner.BindingId ||
            binding.Consumer.ResourceType != owner.ResourceType || binding.Consumer.ResourceId != owner.ResourceId)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        ResourceTypeId ownerType;
        try
        {
            ownerType = ResourceTypeId.Create(owner.ResourceType);
        }
        catch (ArgumentException)
        {
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());
        }

        var registration = await loadRegistration(new(request.TenantId, ownerType, owner.ResourceId), session,
            cancellationToken);
        if (registration is null || registration.Resource.ResourceGroupId != current.ResourceGroupId)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        return new ManagedRegisteredResourceOwnership(ownerType, owner.ResourceId, version);
    }

    public async Task<FenceDeclarationOutcome> FenceRegistrationOwnershipAsync(
        FenceRegisteredResourceOwnershipRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Registration ownership fencing requires a transaction.");

        var ownership = await ReadRegistrationOwnershipAsync(new(request.TenantId, request.ResourceId), session,
            cancellationToken);
        if (ownership is DeclarationReadBlocked blocked)
            return blocked;

        var current = await secrets.Find(session, SecretFilters.Identity(request.TenantId, request.ResourceId))
            .SingleAsync(cancellationToken);
        return SecretPlanningFence.Matches(current, request.Expected) &&
               await planningFence.FenceAsync(current, session, cancellationToken)
            ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
    }
}
