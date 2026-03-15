using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pole.ResultType;

public abstract record Result
{
    public static Success Success() => new();
    public static Success<T> Success<T>(T value) where T : notnull => new(value);

    public static Failure Failure(string message,
    [CallerMemberName] string callerName = "",
    [CallerFilePath] string callerFile = "",
    [CallerLineNumber] int callerLine = 0) => new()
    {
        Message = message,
        CallerName = callerName,
        CallerFile = callerFile,
        CallerLine = callerLine
    };

    public static NotFoundFailure NotFound(string message,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new()
    {
        Message = message,
        CallerName = callerName,
        CallerFile = callerFile,
        CallerLine = callerLine
    };

    public static ValidationFailure ValidationFailure(IDictionary<string,string[]> validationErrors,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0
    ) => new(validationErrors)
    {
        Message = "Validation failed.",
        CallerName = "",
        CallerFile = "",
        CallerLine = 0
    };
    
    public static UnauthorizedFailure Unauthorized(string message,
        string action,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(action)
    {
        Message = message,
        CallerName = callerName,
        CallerFile = callerFile,
        CallerLine = callerLine
    };
    
    public static ConflictFailure Conflict(string message,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new()
    {
        Message = message,
        CallerName = callerName,
        CallerFile = callerFile,
        CallerLine = callerLine
    };
    
    public static InternalFailure Internal(string message,
        string details = "",
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(details)
    {
        Message = message,
        CallerName = callerName,
        CallerFile = callerFile,
        CallerLine = callerLine
    };
    public static ExternalServiceFailure ExternalService(string message,
        string serviceName,
        string details,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(serviceName, details)
    {
        Message = message,
        CallerName = callerName,
        CallerFile = callerFile,
        CallerLine = callerLine
    };
}

public record Result<T> : Result where T : notnull
{
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
                failure = failureResult;
                return false;
            default:
                throw new InvalidOperationException("Unknown Result type");
        }
    }

    public static implicit operator Result<T>(Failure failure) => new Failure<T>
    {
        Message = failure.Message,
        CallerName = failure.CallerName,
        CallerFile = failure.CallerFile,
        CallerLine = failure.CallerLine,
    };
    public static implicit operator Result<T>(T value) => new Success<T>(value);
}

public record Success : Result, ISuccess;

public record Success<T>(T Value) : Result<T>, ISuccess where T : notnull
{
    public static implicit operator T(Success<T> success) => success.Value;
    public override string ToString() => Value?.ToString() ?? base.ToString();
}

public record Failure : Result, IFailure
{
    public required string Message { get; init; }
    public required string CallerName { get; init; }
    public required string CallerFile { get; init; }
    public required int CallerLine { get; init; }
    public override string ToString() => Message;
}

public record Failure<T> : Result<T>, IFailure where T : notnull
{
    public required string Message { get; init; }
    public required string CallerName { get; init; }
    public required string CallerFile { get; init; }
    public required int CallerLine { get; init; }
    
    public static implicit operator Failure<T>(Failure failure) => new()
    {
        Message = failure.Message,
        CallerName = failure.CallerName,
        CallerFile = failure.CallerFile,
        CallerLine = failure.CallerLine,
    };
    public static implicit operator Failure(Failure<T> failure) => new()
    {
        Message = failure.Message,
        CallerName = failure.CallerName,
        CallerFile = failure.CallerFile,
        CallerLine = failure.CallerLine,
    };
    public override string ToString() => Message;
}

public interface ISuccess { }

public interface IFailure
{
    public string Message { get; init; }
    public string CallerName { get; init; }
    public string CallerFile { get; init; }
    public int CallerLine { get; init; }
}

public record NotFoundFailure() : Failure
{
}

public record ValidationFailure(IDictionary<string, string[]> ValidationErrors) : Failure
{
    public override string ToString() => "Validation failed with errors";
}

public record UnauthorizedFailure(string Action) : Failure
{
    public UnauthorizedFailure() : this("Action") { }

    public override string ToString() => $"Unauthorized to perform action: {Action}. Message: {Message}";
}

public record ConflictFailure() : Failure;

public record InternalFailure(string Details) : Failure
{
    public InternalFailure() : this("An internal error occurred.") { }

    public override string ToString() => $"Internal failure: {Details}.";
}

public record ExternalServiceFailure(string ServiceName, string Details) : Failure
{
    public ExternalServiceFailure() : this("ExternalService", "An error occurred with the external service.") { }

    public override string ToString() => $"External service failure ({ServiceName}): {Details}.";
}

