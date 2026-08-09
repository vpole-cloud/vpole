using Pole.ResultType;
using Xunit.Sdk;

namespace Pole.Tests.Integration.Infrastructure;

public static class ResultAssertionExtensions
{
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
