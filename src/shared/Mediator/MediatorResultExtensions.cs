namespace Pole.Mediator;

public static class MediatorResultExtensions
{
    /// <summary>
    ///     Projects either branch without exposing the preview union's underlying value access
    ///     to transport adapters and other generic infrastructure.
    /// </summary>
    public static TResult Match<TOutcome, TResult>(
        this MediatorResult<TOutcome> result,
        Func<TOutcome, TResult> onOutcome,
        Func<PipelineRejected, TResult> onRejected)
        where TOutcome : notnull
    {
        ArgumentNullException.ThrowIfNull(onOutcome);
        ArgumentNullException.ThrowIfNull(onRejected);

        return result switch
        {
            TOutcome => onOutcome((TOutcome)result.Value),
            PipelineRejected rejected => onRejected(rejected),
        };
    }
}
