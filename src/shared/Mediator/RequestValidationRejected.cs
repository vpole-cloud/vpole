using System.Collections.ObjectModel;

namespace Pole.Mediator;

/// <summary>
///     Stops request processing when deterministic request validation fails.
/// </summary>
public sealed record RequestValidationRejected : IMediatorRejection
{
    public RequestValidationRejected(IReadOnlyDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
            throw new ArgumentException("At least one validation error is required.", nameof(errors));

        if (errors.Any(error =>
                error.Value is null || error.Value.Length == 0 || error.Value.Any(message => message is null)))
            throw new ArgumentException(
                "Each validation field must contain at least one non-null message.",
                nameof(errors));

        Errors = new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            errors.ToDictionary(
                error => error.Key,
                error => (IReadOnlyList<string>)Array.AsReadOnly(error.Value.ToArray())));
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Errors { get; }
}
