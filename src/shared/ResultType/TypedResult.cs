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
    /// <summary>
    /// F#-friendly static extraction overload. Call through this extension class because
    /// preview unions synthesize an instance method with the same name.
    /// </summary>
    public static (bool Succeeded, T? Value, TError? Error) TryGetValue<T, TError>(
        this Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result switch
        {
            Ok<T> success => (true, success.Value, default),
            Err<TError> failure => (false, default, failure.Value)
        };

    public static bool TryGetValue<T, TError>(
        this Result<T, TError> result,
        [MaybeNullWhen(false)] out T value,
        [MaybeNullWhen(true)] out TError error)
        where T : notnull
        where TError : notnull
    {
        var extracted = TypedResultExtensions.TryGetValue(result);

        value = extracted.Value!;
        error = extracted.Error!;
        return extracted.Succeeded;
    }
}
