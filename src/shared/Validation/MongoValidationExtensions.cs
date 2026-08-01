using FluentValidation;
using MongoDB.Bson;

namespace Pole.Validation;

public static class MongoValidationExtensions
{
    public static IRuleBuilderOptions<T, string?> MongoObjectId<T>(
        this IRuleBuilder<T, string?> ruleBuilder,
        string displayName) =>
        ruleBuilder
            .Must(value => ObjectId.TryParse(value?.Trim(), out _))
            .WithMessage($"{displayName} must be a MongoDB ObjectId.");

    public static IRuleBuilderOptions<T, string?> OptionalMongoObjectId<T>(
        this IRuleBuilder<T, string?> ruleBuilder,
        string displayName) =>
        ruleBuilder
            .Must(value =>
                string.IsNullOrWhiteSpace(value) ||
                ObjectId.TryParse(value.Trim(), out _))
            .WithMessage($"{displayName} must be a MongoDB ObjectId.");
}
