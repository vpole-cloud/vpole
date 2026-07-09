using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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

public record GenericFailure(
    string Message,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Failure(Message, FailureLocation, InnerFailures);

public record NotFoundFailure(
    string Message,
    object? Id,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Failure(Message, FailureLocation, InnerFailures);

public record ValidationFailure(
    string Message,
    ImmutableDictionary<string, string[]> ValidationErrors,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Failure(Message, FailureLocation, InnerFailures);

public record UnauthorizedFailure(
    string Message,
    string Action,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Failure(Message, FailureLocation, InnerFailures);

public record ForbiddenFailure(
    string Message,
    string Action,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Failure(Message, FailureLocation, InnerFailures);

public record ConflictFailure(
    string Message,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Failure(Message, FailureLocation, InnerFailures);

public record InternalFailure(
    string Message,
    string Details,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Failure(Message, FailureLocation, InnerFailures);

public record ExternalServiceFailure(
    string Message,
    string ServiceName,
    string Details,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures
) : Failure(Message, FailureLocation, InnerFailures);

public static class Failures
{
    public static GenericFailure Failure(string message,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static NotFoundFailure NotFound(string message,
        object? id = null,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        id,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static ValidationFailure ValidationFailure(IDictionary<string, string[]> validationErrors,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        "Validation failed.",
        validationErrors.ToImmutableDictionary(),
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static UnauthorizedFailure Unauthorized(string message,
        string action,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        action,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static ForbiddenFailure Forbidden(string message,
        string action,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        action,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static ConflictFailure Conflict(string message,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static InternalFailure Internal(string message,
        string details = "",
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        details,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static ExternalServiceFailure ExternalService(string message,
        string serviceName,
        string details,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        serviceName,
        details,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    private static FailureLocation CreateLocation(string callerName, string callerFile, int callerLine) =>
        new(callerName, callerFile, callerLine);
}
