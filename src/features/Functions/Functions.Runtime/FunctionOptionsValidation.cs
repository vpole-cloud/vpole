using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Pole.Features.Functions.Runtime;

internal static class FunctionOptionsValidation
{
    public static List<string> GetDataAnnotationFailures<TOptions>(TOptions options)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(options);
        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            validationResults,
            validateAllProperties: true);
        return validationResults
            .Select(result => result.ErrorMessage ?? "Function runtime options are invalid.")
            .ToList();
    }

    public static ValidateOptionsResult ToValidateOptionsResult(IReadOnlyCollection<string> failures) =>
        failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
}
