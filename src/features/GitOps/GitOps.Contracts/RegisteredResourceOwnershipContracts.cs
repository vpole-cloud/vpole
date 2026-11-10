using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.GitOps.Contracts;

public sealed record ReadRegisteredResourceOwnershipRequest(
    string TenantId,
    string ResourceId
);

public sealed record IndependentRegisteredResourceOwnership(
    DeclarationVersionToken Version
);

public sealed record ManagedRegisteredResourceOwnership(
    ResourceTypeId OwnerType,
    string OwnerId,
    DeclarationVersionToken Version
);

public union ReadRegisteredResourceOwnershipOutcome(
    IndependentRegisteredResourceOwnership,
    ManagedRegisteredResourceOwnership,
    DeclarationReadBlocked);

public sealed record FenceRegisteredResourceOwnershipRequest(
    string TenantId,
    string ResourceId,
    DeclarationVersionToken Expected
);

// A generated registration is finalized by its concrete owner, rather than as a separate declaration.
public interface IDeclarativeRegistrationOwnershipProvider
{
    Task<ReadRegisteredResourceOwnershipOutcome> ReadRegistrationOwnershipAsync(
        ReadRegisteredResourceOwnershipRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

    Task<FenceDeclarationOutcome> FenceRegistrationOwnershipAsync(
        FenceRegisteredResourceOwnershipRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}
