using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Pole.ResultType;

/// <summary>
/// An expected application outcome that may safely cross an application boundary.
/// Transport adapters decide how each concrete failure is represented externally.
/// </summary>
public abstract record ApplicationFailure(
    string Message,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : Failure(Message, FailureLocation, InnerFailures);

public abstract record FieldValidationFailure(
    string Message,
    ImmutableDictionary<string, string[]> ValidationErrors,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : ApplicationFailure(Message, FailureLocation, InnerFailures);

public sealed record GenericFailure(
    string Message,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : Failure(Message, FailureLocation, InnerFailures);

public sealed record NotFoundFailure(
    string Message,
    object? Id,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : ApplicationFailure(Message, FailureLocation, InnerFailures);

public sealed record ValidationFailure(
    string Message,
    ImmutableDictionary<string, string[]> ValidationErrors,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : FieldValidationFailure(
        Message,
        ValidationErrors,
        FailureLocation,
        InnerFailures);

public sealed record SemanticValidationFailure(
    string Message,
    ImmutableDictionary<string, string[]> ValidationErrors,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : FieldValidationFailure(
        Message,
        ValidationErrors,
        FailureLocation,
        InnerFailures);

public sealed record UnauthorizedFailure(
    string Message,
    string Action,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : ApplicationFailure(Message, FailureLocation, InnerFailures);

public sealed record ForbiddenFailure(
    string Message,
    string Action,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : ApplicationFailure(Message, FailureLocation, InnerFailures);

public sealed record ConflictFailure(
    string Message,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : ApplicationFailure(Message, FailureLocation, InnerFailures);

public sealed record InternalFailure(
    string Message,
    string Details,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : Failure(Message, FailureLocation, InnerFailures);

public sealed record ExternalServiceFailure(
    string Message,
    string ServiceName,
    string Details,
    FailureLocation FailureLocation,
    ImmutableList<Failure> InnerFailures)
    : ApplicationFailure(Message, FailureLocation, InnerFailures);

public static class Failures
{
    public static GenericFailure Failure(
        string message,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static NotFoundFailure NotFound(
        string message,
        object? id = null,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        id,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static ValidationFailure ValidationFailure(
        IDictionary<string, string[]> validationErrors,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        "Validation failed.",
        validationErrors.ToImmutableDictionary(),
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static SemanticValidationFailure SemanticValidation(
        IDictionary<string, string[]> validationErrors,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        "The request could not be processed.",
        validationErrors.ToImmutableDictionary(),
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static UnauthorizedFailure Unauthorized(
        string message,
        string action,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        action,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static ForbiddenFailure Forbidden(
        string message,
        string action,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        action,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static ConflictFailure Conflict(
        string message,
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static InternalFailure Internal(
        string message,
        string details = "",
        [CallerMemberName] string callerName = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => new(
        message,
        details,
        CreateLocation(callerName, callerFile, callerLine),
        []);

    public static ExternalServiceFailure ExternalService(
        string message,
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

    private static FailureLocation CreateLocation(
        string callerName,
        string callerFile,
        int callerLine) => new(callerName, callerFile, callerLine);
}
