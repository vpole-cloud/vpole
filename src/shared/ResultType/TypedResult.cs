using System.Diagnostics.CodeAnalysis;

namespace Pole.ResultType;

/// <summary>
/// Represents the success or the provider-owned expected error of an operation.
/// Infrastructure failures remain exceptions.
/// </summary>
public union Result<T, TError>(Ok<T>, Err<TError>)
    where T : notnull
    where TError : notnull;

public sealed record Ok<T>(T Value)
    where T : notnull;

public sealed record Err<TError>(TError Value)
    where TError : notnull;

public static class TypedResultExtensions
{
    public static bool TryGetValue<T, TError>(
        this Result<T, TError> result,
        [MaybeNullWhen(false)] out T value,
        [MaybeNullWhen(true)] out TError error)
        where T : notnull
        where TError : notnull
    {
        var extracted = result switch
        {
            Ok<T> success => (Succeeded: true, Value: success.Value, Error: default(TError)),
            Err<TError> failure => (Succeeded: false, Value: default(T), Error: failure.Value)
        };

        value = extracted.Value!;
        error = extracted.Error!;
        return extracted.Succeeded;
    }
}
