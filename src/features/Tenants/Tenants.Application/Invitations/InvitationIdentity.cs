using System.Security.Cryptography;
using System.Text;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed record InvitationActor(
    string Subject
);

internal static class InvitationIdentity
{
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Slot(string tenantId, string email) => Hash(tenantId + ":" + email);

    public static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public static bool CanGrant(
        Tenant tenant,
        InvitationActor actor,
        TenantRole role)
    {
        var member = tenant.Members.FirstOrDefault(item => item.UserId == actor.Subject);
        return member is not null && member.Role.CanManageMembers() &&
            (role != TenantRole.Owner || member.Role.CanManageTenant());
    }
}

public sealed record InvitationNotFound;

public sealed record InvitationForbidden;

public sealed record InvitationAlreadyPending;

public sealed record InvitationRecipientAlreadyMember;

public sealed record InvitationUnavailable;

public sealed record InvitationIdentityMismatch;

public sealed record InvitationIdentityConflict;

public sealed record InvitationEncryptionUnavailable;

public sealed record InvitationRevisionConflict;
