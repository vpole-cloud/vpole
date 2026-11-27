// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Users.Application.Registration;

public sealed record UserEmailAlreadyExists(
    string Email
);

public sealed record UserIdentityAlreadyExists(
    string UserId
);

public union CreateUserPersistenceError(UserEmailAlreadyExists, UserIdentityAlreadyExists);
