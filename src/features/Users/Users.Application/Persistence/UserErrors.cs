namespace Pole.Features.Users.Application.Persistence;

public sealed record UserEmailAlreadyExists(string Email);

public sealed record UserIdentityAlreadyExists(string UserId);

public union CreateUserPersistenceError(
    UserEmailAlreadyExists,
    UserIdentityAlreadyExists);
