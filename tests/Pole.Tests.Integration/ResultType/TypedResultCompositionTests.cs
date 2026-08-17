using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.ResultType;

public sealed class TypedResultCompositionTests
{
    [Fact]
    public void Map_TransformsSuccessAndPreservesError()
    {
        Result<int, TestError> success = new Ok<int>(2);
        Result<int, TestError> failure = new Err<TestError>(new TestError("failed"));

        var mappedSuccess = success.Map(value => value.ToString());
        var mappedFailure = failure.Map(MustNotMap);

        Assert.Equal("2", AssertSuccess(mappedSuccess));
        Assert.Equal("failed", AssertError(mappedFailure).Code);
    }

    [Fact]
    public void MapError_TransformsErrorAndPreservesSuccess()
    {
        Result<int, TestError> success = new Ok<int>(2);
        Result<int, TestError> failure = new Err<TestError>(new TestError("failed"));

        var mappedSuccess = success.MapError(MustNotMapError);
        var mappedFailure = failure.MapError(error => new OtherError(error.Code));

        Assert.Equal(2, AssertSuccess(mappedSuccess));
        Assert.Equal("failed", AssertError(mappedFailure).Code);
    }

    [Fact]
    public void Then_ContinuesAfterSuccessAndShortCircuitsAfterError()
    {
        Result<int, TestError> success = new Ok<int>(2);
        Result<int, TestError> failure = new Err<TestError>(new TestError("failed"));
        var calls = 0;

        Result<string, TestError> Continue(int value)
        {
            calls++;
            return new Ok<string>(value.ToString());
        }

        var continued = success.Then(Continue);
        var shortCircuited = failure.Then(Continue);

        Assert.Equal("2", AssertSuccess(continued));
        Assert.Equal("failed", AssertError(shortCircuited).Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Ensure_RejectsOnlySuccessfulValuesThatFailThePredicate()
    {
        Result<int, TestError> accepted = new Ok<int>(2);
        Result<int, TestError> rejected = new Ok<int>(1);
        Result<int, TestError> existingFailure =
            new Err<TestError>(new TestError("existing"));

        var acceptedResult = accepted.Ensure(IsEven, value => new TestError($"odd:{value}"));
        var rejectedResult = rejected.Ensure(IsEven, value => new TestError($"odd:{value}"));
        var existingResult = existingFailure.Ensure(
            _ => throw new Xunit.Sdk.XunitException("The predicate must not run after an error."),
            _ => throw new Xunit.Sdk.XunitException("The error factory must not run after an error."));

        Assert.Equal(2, AssertSuccess(acceptedResult));
        Assert.Equal("odd:1", AssertError(rejectedResult).Code);
        Assert.Equal("existing", AssertError(existingResult).Code);
    }

    [Fact]
    public async Task AsyncComposition_WidensErrorAndContinuesOnSuccess()
    {
        Task<Result<int, TestError>> source =
            Task.FromResult<Result<int, TestError>>(new Ok<int>(2));

        var result = await source
            .MapErrorAsync(error => new OtherError(error.Code))
            .ThenAsync(value => Task.FromResult<Result<string, OtherError>>(
                new Ok<string>((value * 2).ToString())));

        Assert.Equal("4", AssertSuccess(result));
    }

    [Fact]
    public async Task AsyncComposition_ShortCircuitsAndPreservesExceptionAndCancellation()
    {
        Task<Result<int, TestError>> failure = Task.FromResult<Result<int, TestError>>(
            new Err<TestError>(new TestError("failed")));
        var called = false;

        var shortCircuited = await failure.ThenAsync(value =>
        {
            called = true;
            return Task.FromResult<Result<string, TestError>>(
                new Ok<string>(value.ToString()));
        });

        Assert.False(called);
        Assert.Equal("failed", AssertError(shortCircuited).Code);

        Task<Result<int, TestError>> success =
            Task.FromResult<Result<int, TestError>>(new Ok<int>(2));
        var expected = new TestException();
        var thrown = await Assert.ThrowsAsync<TestException>(() => success.ThenAsync(
            _ => Task.FromException<Result<string, TestError>>(expected)));
        Assert.Same(expected, thrown);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => success.ThenAsync(
            _ => Task.FromCanceled<Result<string, TestError>>(cancellation.Token)));
    }

    [Fact]
    public void Match_FoldsBothCasesIntoOneOutcome()
    {
        Result<int, TestError> success = new Ok<int>(2);
        Result<int, TestError> failure = new Err<TestError>(new TestError("failed"));

        Assert.Equal("ok:2", success.Match(
            value => $"ok:{value}",
            error => $"error:{error.Code}"));
        Assert.Equal("error:failed", failure.Match(
            value => $"ok:{value}",
            error => $"error:{error.Code}"));
    }

    private static bool IsEven(int value) => value % 2 == 0;

    private static string MustNotMap(int _) =>
        throw new Xunit.Sdk.XunitException("Map must not run after an error.");

    private static OtherError MustNotMapError(TestError _) =>
        throw new Xunit.Sdk.XunitException("MapError must not run after a success.");

    private static T AssertSuccess<T, TError>(Result<T, TError> result)
        where T : notnull
        where TError : notnull
    {
        Assert.True(result.TryGetValue(out var value, out _));
        return value;
    }

    private static TError AssertError<T, TError>(Result<T, TError> result)
        where T : notnull
        where TError : notnull
    {
        Assert.False(result.TryGetValue(out _, out var error));
        return error;
    }

    private sealed record TestError(string Code);

    private sealed record OtherError(string Code);

    private sealed class TestException : Exception;
}
