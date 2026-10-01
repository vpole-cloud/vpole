using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Endpoints;

internal static class PrivateAccessGrantDtoExtensions
{
    public static PrivateAccessGrantDto ToDto(this PrivateAccessGrant grant) => new(
        grant.Id,
        grant.TenantId,
        grant.SubjectUserId,
        grant.ResourceType,
        grant.ResourceId,
        grant.ResourceDeploymentId,
        grant.EndpointId,
        grant.CreatedAtUtc,
        grant.ExpiresAtUtc,
        grant.State is RevokedPrivateAccessGrant);
}
