using Pole.Features.Users.Domain;

namespace Pole.Features.Users.Application.Common;

public sealed record UserDto(
    string Id,
    string Email,
    string? FirstName,
    string? LastName,
    string DefaultTenantId
);

public static class UserDtoExtensions
{
    public static UserDto ToDto(this PoleUser user) => new(
        user.Id,
        user.Email,
        user.FirstName,
        user.LastName,
        user.DefaultTenantId);
}
