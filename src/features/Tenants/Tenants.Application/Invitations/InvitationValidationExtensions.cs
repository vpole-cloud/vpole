using FluentValidation;

namespace Pole.Features.Tenants.Application.Invitations;

public static class InvitationValidationExtensions
{
    public static IRuleBuilderOptions<T, string> InvitationId<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Matches("\\A[0-9A-F]{64}\\z");

    public static IRuleBuilderOptions<T, string> InvitationToken<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Matches("\\A[A-Za-z0-9_-]{43}\\z");
}
