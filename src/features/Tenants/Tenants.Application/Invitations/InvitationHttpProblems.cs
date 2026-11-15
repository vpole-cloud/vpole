using Pole.ASP.Helpers;

namespace Pole.Features.Tenants.Application.Invitations;

internal static class InvitationHttpProblems
{
    public static HttpProblem Forbidden(InvitationForbidden _) => Problem(
        403,
        "forbidden",
        "Invitation management requires tenant authority.");

    public static HttpProblem Unavailable(InvitationUnavailable _) => Problem(
        410,
        "unavailable",
        "Invitation is no longer available.");

    public static HttpProblem IdentityMismatch(InvitationIdentityMismatch _) => Problem(
        403,
        "identity-mismatch",
        "Sign in with the verified invited email address.");

    public static HttpProblem Pending(InvitationAlreadyPending _) => Problem(
        409,
        "already-pending",
        "An active invitation already exists.");

    public static HttpProblem AlreadyMember(InvitationRecipientAlreadyMember _) => Problem(
        409,
        "already-member",
        "The recipient is already a member.");

    public static HttpProblem EncryptionUnavailable(InvitationEncryptionUnavailable _) => Problem(
        503,
        "protection-unavailable",
        "Invitation protection is unavailable.");

    public static HttpProblem IdentitySetupUnavailable(InvitationIdentitySetupUnavailable _) => Problem(
        502,
        "identity-setup-unavailable",
        "Account setup is temporarily unavailable.");

    public static HttpProblem NotFound(InvitationNotFound _) => Problem(404, "not-found", "Invitation not found.");

    private static HttpProblem Problem(
        int status,
        string reason,
        string title)
    {
        var code = "tenants.invitation." + reason;
        return new HttpProblem(status, PoleProblemTypeId.FromCode(code), title, code);
    }
}
