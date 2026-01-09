namespace Pole.Server.Models;

public record User(
    string Username,
    string PasswordHash,
    string PasswordSalt,
    string FullName,
    string Email,
    DateTime CreatedAt
) : BaseEntity;