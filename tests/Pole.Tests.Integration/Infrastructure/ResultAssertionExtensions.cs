using System.Diagnostics.CodeAnalysis;
using Pole.Mediator;
using Pole.ResultType;
using Xunit.Sdk;

namespace Pole.Tests.Integration.Infrastructure;

public static class ResultAssertionExtensions
{
    public static bool TryGetValue<T>(
        this MediatorResult<T> result,
        [MaybeNullWhen(false)] out T value,
        [MaybeNullWhen(true)] out Failure failure)
        where T : notnull
    {
        var extracted = result.Match(
            outcome => (Succeeded: true, Value: outcome, Failure: default(Failure)),
            rejected => rejected.Reason switch
            {
                LegacyResultRejected legacy =>
                    (Succeeded: false, Value: default(T), Failure: legacy.Failure),
                var reason => throw new XunitException(
                    $"The test compatibility adapter cannot unwrap mediator rejection " +
                    $"'{reason.GetType().FullName}'.")
            });

        value = extracted.Value!;
        failure = extracted.Failure!;
        return extracted.Succeeded;
    }

    public static T AssertSuccess<T>(this MediatorResult<T> result)
        where T : notnull
    {
        if (TryGetValue(result, out var value, out var failure))
        {
            return value;
        }

        throw new XunitException(
            $"Expected a successful mediator result, but received " +
            $"{failure.GetType().Name}: {failure.Message}");
    }

    public static Failure AssertFailure<T>(this MediatorResult<T> result)
        where T : notnull
    {
        if (!TryGetValue(result, out _, out var failure))
        {
            return failure;
        }

        throw new XunitException(
            "Expected a failed mediator result, but received an outcome.");
    }

    public static void AssertSuccess(this Result result)
    {
        if (result is ISuccess)
            return;

        if (result is Failure failure)
        {
            throw new XunitException(
                $"Expected a successful result, but received " +
                $"{failure.GetType().Name}: {failure.Message}");
        }

        throw new XunitException(
            $"Expected a successful result, but received unsupported result type " +
            $"'{result.GetType().Name}'.");
    }

    public static T AssertSuccess<T>(this Result<T> result)
        where T : notnull =>
        result switch
        {
            Success<T> success => success.Value,
            Failure<T> failure => throw new XunitException(
                $"Expected a successful result, but received " +
                $"{failure.Error.GetType().Name}: {failure.Error.Message}"),
            _ => throw new XunitException(
                $"Expected a successful result, but received unsupported result type " +
                $"'{result.GetType().Name}'.")
        };

    public static Failure AssertFailure(this Result result) =>
        result switch
        {
            Failure failure => failure,
            ISuccess success => throw new XunitException(
                $"Expected a failed result, but received {success.GetType().Name}."),
            _ => throw new XunitException(
                $"Expected a failed result, but received unsupported result type " +
                $"'{result.GetType().Name}'.")
        };

    public static Failure AssertFailure<T>(this Result<T> result)
        where T : notnull =>
        result switch
        {
            Failure<T> failure => failure.Error,
            Success<T> success => throw new XunitException(
                $"Expected a failed result, but received {success.GetType().Name}."),
            _ => throw new XunitException(
                $"Expected a failed result, but received unsupported result type " +
                $"'{result.GetType().Name}'.")
        };
}
