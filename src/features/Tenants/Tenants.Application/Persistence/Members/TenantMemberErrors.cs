using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants;

namespace Pole.Features.Tenants.Application.Persistence;

public sealed record TenantMemberAlreadyExists(
    string TenantId,
    string UserId);

public sealed record TenantLastOwner(
    string TenantId,
    string MemberId);

public union TenantMemberAdditionError(
    TenantNotFound,
    TenantMemberAlreadyExists);

public union TenantMemberMutationError(
    TenantNotFound,
    TenantMemberNotFound,
    TenantLastOwner);
