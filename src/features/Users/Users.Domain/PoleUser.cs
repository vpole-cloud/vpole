namespace Pole.Features.Users.Domain;

public sealed record PoleUser(
    string Id,
    string Email,
    string? FirstName,
    string? LastName,
    string DefaultTenantId,
    DateTime CreatedAtUtc);
