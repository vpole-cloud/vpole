using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Application.Mongo;
namespace Pole.Features.Tenants.Application.Members;

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
