using System.Diagnostics.CodeAnalysis;
using System.Collections.Immutable;

namespace Pole.ResultType;

public abstract record Result
{
    public static Success Success() => new();
    public static Success<T> Success<T>(T value) where T : notnull => new(value);
}

public abstract record Result<T> : Result where T : notnull
{
    public static implicit operator Result<T>(Failure failure) => new Failure<T>(failure);
    public static implicit operator Result<T>(T value) => new Success<T>(value);

    public (bool Succeeded, T? Value, Failure? Failure) TryGetValue()
    {
        return this switch
        {
            Success<T> success => (true, success.Value, null),
            Failure<T> failureResult => (false, default, failureResult.Error),
            _ => throw new InvalidOperationException("Unknown Result type"),
        };
    }

    public bool TryGetValue([MaybeNullWhen(false)] out T value, [MaybeNullWhen(true)] out Failure failure)
    {
        switch (this)
        {
            case Success<T> success:
                value = success.Value;
                failure = null!;
                return true;
            case Failure<T> failureResult:
                value = default!;
                failure = failureResult.Error;
                return false;
            default:
                throw new InvalidOperationException("Unknown Result type");
        }
    }
}

public record Success : Result, ISuccess;

public record Success<T>(T Value) : Result<T>, ISuccess where T : notnull
{
    public static implicit operator T(Success<T> success) => success.Value;
    public override string ToString() => Value.ToString() ?? base.ToString();
}

public abstract record Failure(
    string Message,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Result, IFailure;

public sealed record FailureLocation(
    string CallerName,
    string CallerFile,
    int CallerLine
);

public record Failure<T>(Failure Error) : Result<T> where T : notnull
{
    public static implicit operator Failure<T>(Failure failure) => new(failure);
    public static implicit operator Failure(Failure<T> failure) => failure.Error;
}

public interface ISuccess { }

public interface IFailure
{
    string Message { get; }
    FailureLocation FailureLocation { get; }
    ImmutableList<Failure> InnerFailures { get; }
}
