using System.Diagnostics.CodeAnalysis;

namespace Pole.ResultType;

/// <summary>
/// Represents the success or the provider-owned expected error of an operation.
/// Infrastructure failures remain exceptions.
/// </summary>
public abstract record Result<T, TError>
    where T : notnull
    where TError : notnull
{
    public bool TryGetValue(
        [MaybeNullWhen(false)] out T value,
        [MaybeNullWhen(true)] out TError error)
    {
        switch (this)
        {
            case Success<T, TError> success:
                value = success.Value;
                error = default!;
                return true;
            case Error<T, TError> failure:
                value = default!;
                error = failure.Value;
                return false;
            default:
                throw new InvalidOperationException("Unknown typed Result type.");
        }
    }
}

public sealed record Success<T, TError>(T Value) : Result<T, TError>
    where T : notnull
    where TError : notnull;

public sealed record Error<T, TError>(TError Value) : Result<T, TError>
    where T : notnull
    where TError : notnull;
