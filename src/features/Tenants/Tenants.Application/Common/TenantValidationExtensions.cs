using FluentValidation;
using MongoDB.Bson;

namespace Pole.Features.Tenants.Application.Common;

public static class TenantValidationExtensions
{
    public static IRuleBuilderOptions<T, string> RouteObjectId<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(value => ObjectId.TryParse(value, out _));

    public static IRuleBuilderOptions<T, string> InvitationId<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Matches("\\A[0-9A-F]{64}\\z");

    public static IRuleBuilderOptions<T, string> InvitationToken<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Matches("\\A[A-Za-z0-9_-]{43}\\z");
}
