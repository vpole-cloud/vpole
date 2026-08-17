namespace Pole.ResultType;

/// <summary>
/// Composes expected outcomes without catching exceptions or cancellation. Transaction and
/// transport decisions remain the responsibility of their owning boundaries.
/// </summary>
public static class TypedResultCompositionExtensions
{
    public static Result<TNext, TError> Map<T, TNext, TError>(
        this Result<T, TError> result,
        Func<T, TNext> map)
        where T : notnull
        where TNext : notnull
        where TError : notnull =>
        result switch
        {
            Ok<T> success => new Ok<TNext>(map(success.Value)),
            Err<TError> error => error
        };

    public static Result<T, TNextError> MapError<T, TError, TNextError>(
        this Result<T, TError> result,
        Func<TError, TNextError> mapError)
        where T : notnull
        where TError : notnull
        where TNextError : notnull =>
        result switch
        {
            Ok<T> success => success,
            Err<TError> error => new Err<TNextError>(mapError(error.Value))
        };

    public static Result<TNext, TError> Then<T, TNext, TError>(
        this Result<T, TError> result,
        Func<T, Result<TNext, TError>> next)
        where T : notnull
        where TNext : notnull
        where TError : notnull =>
        result switch
        {
            Ok<T> success => next(success.Value),
            Err<TError> error => error
        };

    public static Result<T, TError> Ensure<T, TError>(
        this Result<T, TError> result,
        Func<T, bool> predicate,
        Func<T, TError> errorFactory)
        where T : notnull
        where TError : notnull =>
        result switch
        {
            Ok<T> success when predicate(success.Value) => success,
            Ok<T> success => new Err<TError>(errorFactory(success.Value)),
            Err<TError> error => error
        };

    public static TOutcome Match<T, TError, TOutcome>(
        this Result<T, TError> result,
        Func<T, TOutcome> onSuccess,
        Func<TError, TOutcome> onError)
        where T : notnull
        where TError : notnull
        where TOutcome : notnull =>
        result switch
        {
            Ok<T> success => onSuccess(success.Value),
            Err<TError> error => onError(error.Value)
        };

    public static async Task<Result<T, TNextError>> MapErrorAsync<T, TError, TNextError>(
        this Task<Result<T, TError>> resultTask,
        Func<TError, TNextError> mapError)
        where T : notnull
        where TError : notnull
        where TNextError : notnull =>
        (await resultTask).MapError(mapError);

    public static async Task<Result<TNext, TError>> ThenAsync<T, TNext, TError>(
        this Task<Result<T, TError>> resultTask,
        Func<T, Task<Result<TNext, TError>>> next)
        where T : notnull
        where TNext : notnull
        where TError : notnull
    {
        var result = await resultTask;
        return result switch
        {
            Ok<T> success => await next(success.Value),
            Err<TError> error => error
        };
    }
}
