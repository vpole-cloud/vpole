using FluentValidation;
using MongoDB.Bson;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Functions.Application.Common;

public sealed class FunctionConfigurationValidator : AbstractValidator<FunctionConfiguration>
{
    public FunctionConfigurationValidator()
    {
        RuleFor(configuration => configuration.EnvironmentVariables).NotNull();
        RuleFor(configuration => configuration.SecretReferences).NotNull();
        RuleFor(configuration => configuration.SecretReferences)
            .Must(references => references is not null && references.Values.All(reference =>
                reference is not null && ObjectId.TryParse(reference.SecretId, out _) &&
                SecretKeyReferenceRules.ValidKey(reference.Key)))
            .WithMessage("Secret references require a Secret ID and a valid key.");
        RuleFor(configuration => configuration)
            .Must(HasValidEnvironmentNames)
            .WithMessage(
                "Configuration keys must be valid environment variable names and cannot override Pole runtime settings."
            );
        RuleFor(configuration => configuration)
            .Must(HasNoDuplicateKeys)
            .WithMessage("Environment variables and secret references cannot use the same key.");
    }

    private static bool HasValidEnvironmentNames(FunctionConfiguration configuration) =>
        configuration.EnvironmentVariables is not null &&
        configuration.SecretReferences is not null &&
        configuration.EnvironmentVariables.Keys
            .Concat(configuration.SecretReferences.Keys)
            .All(IsAllowedEnvironmentName);

    private static bool HasNoDuplicateKeys(FunctionConfiguration configuration) =>
        configuration.EnvironmentVariables is not null &&
        configuration.SecretReferences is not null &&
        !configuration.EnvironmentVariables.Keys.Intersect(
                configuration.SecretReferences.Keys,
                StringComparer.OrdinalIgnoreCase)
            .Any();

    internal static bool IsAllowedEnvironmentName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsLetterOrDigit(character) || character == '_') &&
        !name.StartsWith("PoleFunctions__", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("DOTNET_EnableDiagnostics", StringComparison.OrdinalIgnoreCase);
}

public sealed class FunctionResourcesValidator : AbstractValidator<FunctionResources>
{
    public FunctionResourcesValidator()
    {
        RuleFor(resources => resources.Requests).NotNull().SetValidator(new FunctionResourceValuesValidator());
        RuleFor(resources => resources.Limits).NotNull().SetValidator(new FunctionResourceValuesValidator());
        RuleFor(resources => resources).Must(resources =>
                resources.Requests is not null &&
                resources.Limits is not null &&
                resources.Limits.CpuMillis >= resources.Requests.CpuMillis &&
                resources.Limits.MemoryMi >= resources.Requests.MemoryMi)
            .WithMessage("Resource limits cannot be lower than requests.");
    }
}

public sealed class FunctionResourceValuesValidator : AbstractValidator<FunctionResourceValues>
{
    public FunctionResourceValuesValidator()
    {
        RuleFor(resources => resources.CpuMillis).GreaterThan(0);
        RuleFor(resources => resources.MemoryMi).GreaterThan(0);
    }
}

public sealed class FunctionScalingPolicyValidator : AbstractValidator<FunctionScalingPolicy>
{
    public FunctionScalingPolicyValidator()
    {
        RuleFor(scaling => scaling.MinInstances).GreaterThanOrEqualTo(0);
        RuleFor(scaling => scaling.MaxInstances).GreaterThan(0);
        RuleFor(scaling => scaling)
            .Must(value => value.MinInstances <= value.MaxInstances)
            .WithMessage("MinInstances cannot exceed MaxInstances.");
        RuleFor(scaling => scaling.TargetConcurrencyPerInstance).GreaterThan(0);
        RuleFor(scaling => scaling.MaxConcurrencyPerInstance).GreaterThan(0);
        RuleFor(scaling => scaling.ScaleDownDelaySeconds).InclusiveBetween(0, 3600);
        RuleFor(scaling => scaling.ActivationTimeoutSeconds).InclusiveBetween(
            FunctionScalingPolicy.MinimumActivationTimeoutSeconds,
            FunctionScalingPolicy.MaximumActivationTimeoutSeconds);
        RuleFor(scaling => scaling)
            .Must(value => value.TargetConcurrencyPerInstance <= value.MaxConcurrencyPerInstance)
            .WithMessage("Target concurrency cannot exceed maximum concurrency.");
    }
}
