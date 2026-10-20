namespace Pole.Testing;

/// <summary>Arm an observer before an action, then bound only its post-action completion.</summary>
public sealed class TestActionObservation<T>
{
    private readonly TaskCompletionSource<T> _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryObserve(T value) => _observed.TrySetResult(value);

    public async Task<T> RunAsync(
        Func<Task> action,
        TimeSpan completionTimeout,
        CancellationToken cancellationToken)
    {
        await action().WaitAsync(cancellationToken);
        return await _observed.Task.WaitAsync(completionTimeout, cancellationToken);
    }
}
