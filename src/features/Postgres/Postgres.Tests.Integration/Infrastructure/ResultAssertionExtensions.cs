using Pole.Features.Request;
using Pole.Mediator;
using Pole.ResultType;
using Xunit.Sdk;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public static class ResultAssertionExtensions
{
    public static T AssertSuccess<T>(this MediatorResult<T> result) where T : notnull =>
        result.Match(outcome => outcome, rejected => throw new XunitException($"Expected a handler outcome, but received '{rejected.Reason.GetType().Name}'."));

    public static RequestValidationRejected AssertValidationRejected<T>(this MediatorResult<T> result) where T : notnull =>
        result.Match(_ => throw new XunitException("Expected request validation rejection."), rejected => rejected.Reason as RequestValidationRejected ?? throw new XunitException($"Expected validation rejection, but received '{rejected.Reason.GetType().Name}'."));

    public static T AssertSuccess<T, TError>(this Result<T, TError> result) where T : notnull where TError : notnull => result switch { Ok<T> value => value.Value, Err<TError> error => Fail<T>(error.Value), _ => throw new XunitException("Unsupported result.") };
    public static TError AssertFailure<T, TError>(this Result<T, TError> result) where T : notnull where TError : notnull => result switch { Err<TError> value => value.Value, Ok<T> success => Fail<TError>(success.Value), _ => throw new XunitException("Unsupported result.") };

    private static T Fail<T>(object value) => throw new XunitException($"Unexpected result: {System.Text.Json.JsonSerializer.Serialize(value, value.GetType())}");
}
