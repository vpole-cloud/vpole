// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Users.Application.Identity;

public sealed record CreateIdentityUser(
    string Email,
    string? FirstName,
    string? LastName
);

public sealed record IdentityUserCreationFailed;

public sealed record IdentityUserCreated(
    string UserId
);

public union CreateIdentityUserOutcome(IdentityUserCreated, IdentityUserCreationFailed);

public interface IIdentityProvider
{
    Task<CreateIdentityUserOutcome> CreateUserAsync(CreateIdentityUser user, CancellationToken cancellationToken);

    Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken);
}
