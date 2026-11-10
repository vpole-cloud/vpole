using Pole.Mediator;

namespace Pole.Features.Tenants.Application;

public sealed record TenantAuthenticationRequired : IMediatorRejection;

public sealed record TenantUserIdentityRequired : IMediatorRejection;

public sealed record TenantNotFound(
    string TenantId
) : IMediatorRejection;

public sealed record TenantMembershipRequired(
    string TenantId
) : IMediatorRejection;

public sealed record TenantMemberNotFound(
    string TenantId,
    string MemberId
) : IMediatorRejection;
