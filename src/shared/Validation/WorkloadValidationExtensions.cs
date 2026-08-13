using FluentValidation;
using Pole.Workloads;

namespace Pole.Validation;

public static class WorkloadValidationExtensions
{
    public static IRuleBuilderOptions<T, string?> ResourceTypeId<T>(
        this IRuleBuilder<T, string?> ruleBuilder,
        string displayName) =>
        ruleBuilder
            .Must(value => Pole.Workloads.ResourceTypeId.TryCreate(value, out _))
            .WithMessage($"{displayName} is invalid.");
}
