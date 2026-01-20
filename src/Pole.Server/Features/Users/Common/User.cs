using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.Users.Common;

public record User(
    string Username,
    string FullName,
    string Email,
    DateTime CreatedAt
) : BaseEntity;