namespace Pole.Features.Users.Application.Registration;

public sealed record UserEmailAlreadyExists(
    string Email
);

public sealed record UserIdentityAlreadyExists(
    string UserId
);

public union CreateUserPersistenceError(UserEmailAlreadyExists, UserIdentityAlreadyExists);
