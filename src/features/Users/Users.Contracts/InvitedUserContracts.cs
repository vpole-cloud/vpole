using MongoDB.Driver;

namespace Pole.Features.Users.Contracts;

public sealed record JoinInvitedUserRequest(
    string Subject,
    string VerifiedEmail,
    string TenantId,
    string? FirstName,
    string? LastName
);

public sealed record InvitedUserJoined(
    string DefaultTenantId
);

public sealed record InvitedUserIdentityConflict;

public union JoinInvitedUserOutcome(InvitedUserJoined, InvitedUserIdentityConflict);

/// <summary>
/// Creates an invited Pole user without a personal tenant. Serializes simultaneous first joins
/// on the subject's user document and participates in the caller's membership transaction.
/// </summary>
public delegate Task<JoinInvitedUserOutcome> JoinInvitedUser(
    JoinInvitedUserRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public sealed record StartInvitedIdentityRequest(
    string Email,
    string RedirectUri
);

public sealed record InvitedIdentityStarted(
    string Subject,
    bool SetupEmailSent
);

public sealed record InvitedIdentityUnavailable;

public union StartInvitedIdentityOutcome(InvitedIdentityStarted, InvitedIdentityUnavailable);

/// <summary>Only a user-initiated onboarding action may invoke this operation.</summary>
public delegate Task<StartInvitedIdentityOutcome> StartInvitedIdentity(
    StartInvitedIdentityRequest request,
    CancellationToken cancellationToken);

public sealed record ReconcileUserDefaultTenantRequest(
    string Subject,
    IReadOnlyList<string> RemainingTenantIds
);

public delegate Task ReconcileUserDefaultTenant(
    ReconcileUserDefaultTenantRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
