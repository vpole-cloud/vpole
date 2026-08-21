using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

internal static class TypedComputeProfileResultAssertions
{
    public static T AssertSuccess<T, TError>(this Result<T, TError> result)
        where T : notnull
        where TError : notnull => result switch
    {
        Ok<T> success => success.Value,
        Err<TError> error => throw new Xunit.Sdk.XunitException(
            $"Expected success, but received {error.Value.GetType().Name}.")
    };

    public static TError AssertError<T, TError>(this Result<T, TError> result)
        where T : notnull
        where TError : notnull => result switch
    {
        Ok<T> success => throw new Xunit.Sdk.XunitException(
            $"Expected an error, but received {success.Value.GetType().Name}."),
        Err<TError> error => error.Value
    };
}
