namespace Pole.Features.Users.Domain;

public sealed record PoleUser(
    string Id,
    string Email,
    string? FirstName,
    string? LastName,
    string DefaultTenantId,
    DateTime CreatedAtUtc,
    long MembershipFence
);

public static class PoleUserFactory
{
    public static PoleUser Create(
        string id,
        string email,
        string? firstName,
        string? lastName,
        string defaultTenantId,
        DateTime createdAtUtc) => new(
        Id: id,
        Email: email,
        FirstName: firstName,
        LastName: lastName,
        DefaultTenantId: defaultTenantId,
        CreatedAtUtc: createdAtUtc,
        MembershipFence: 0);
}
