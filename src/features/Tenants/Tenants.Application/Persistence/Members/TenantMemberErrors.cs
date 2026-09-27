namespace Pole.Features.Tenants.Application.Persistence.Members;

public sealed record TenantMemberAlreadyExists(
    string TenantId,
    string UserId);

public sealed record TenantLastOwner(
    string TenantId,
    string MemberId);

public union TenantMemberMutationError(
    TenantNotFound,
    TenantMemberNotFound,
    TenantLastOwner);
