using System.Diagnostics;
using k8s.Autorest;

namespace Pole.Testing;

public sealed class TestResourceScope : IAsyncDisposable
{
    private readonly List<CleanupAction> _cleanupActions = [];
    private readonly TimeSpan _cleanupTimeout;
    private readonly string _description;
    private readonly Lock _lock = new();
    private readonly TestTimings? _timings;
    private bool _disposeStarted;

    public TestResourceScope(
        string description,
        TimeSpan? cleanupTimeout = null,
        TestTimings? timings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (cleanupTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout), "Cleanup timeout must be positive.");

        _description = description;
        _cleanupTimeout = cleanupTimeout ?? TimeSpan.FromMinutes(3);
        _timings = timings;
    }

    public async ValueTask DisposeAsync()
    {
        CleanupAction[] cleanupActions;
        lock (_lock)
        {
            if (_disposeStarted)
                return;

            _disposeStarted = true;
            cleanupActions = _cleanupActions.AsEnumerable().Reverse().ToArray();
        }

        using var timeout = new CancellationTokenSource(_cleanupTimeout);
        using var timing = _timings?.Measure("cleanup.total");
        var failures = new List<Exception>();
        foreach (var cleanupAction in cleanupActions)
        {
            var failure = await ExecuteCleanupAsync(cleanupAction, timeout.Token, _timings);
            if (failure is not null)
                failures.Add(failure);
        }

        if (failures.Count > 0)
            throw new AggregateException($"Cleanup for '{_description}' failed.", failures);
    }

    public async Task<T> CreateAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> create,
        Func<T, CancellationToken, Task> cleanup,
        CancellationToken cancellationToken = default,
        string timingPhase = "cleanup.action")
    {
        ValidateDescription(description);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(cleanup);
        ThrowIfDisposeStarted();

        var resource = await create(cancellationToken);
        var cleanupAction = new CleanupAction(description, token => cleanup(resource, token), timingPhase);
        if (TryRegister(cleanupAction))
            return resource;

        await ThrowRegistrationRaceAsync(cleanupAction);
        throw new UnreachableException();
    }

    // Timing phases are fixed code-owned names; descriptions may contain resource identifiers.
    public void Defer(
        string description,
        Func<CancellationToken, Task> cleanup,
        string timingPhase = "cleanup.action")
    {
        ValidateDescription(description);
        ArgumentNullException.ThrowIfNull(cleanup);

        if (!TryRegister(new CleanupAction(description, cleanup, timingPhase)))
            throw new ObjectDisposedException(nameof(TestResourceScope));
    }

    public TestResourceCleanupGroup CreateConcurrentCleanupGroup(string description, int maxConcurrency = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        var group = new TestResourceCleanupGroup(description, _cleanupTimeout, maxConcurrency, _timings);
        Defer(description, group.CleanupAsync, "cleanup.group.concurrent");
        return group;
    }

    public TestResourceCleanupGroup CreateCleanupSequence(string description)
    {
        var group = new TestResourceCleanupGroup(description, _cleanupTimeout, 1, _timings);
        Defer(description, group.CleanupAsync, "cleanup.group.sequence");
        return group;
    }

    public async Task RunAsync(Func<Task> body)
    {
        ThrowIfDisposeStarted();
        ArgumentNullException.ThrowIfNull(body);
        var journal = TestPhaseJournal.Current ?? new TestPhaseJournal(_description);
        using var activation = journal.Activate();
        Exception? primaryFailure = null;
        try
        {
            using (_timings?.Measure("test.body"))
            {
                await body();
            }

            journal.Record(TestPhase.BodyCompleted);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            journal.Record(TestPhase.Failed, status: FailureStatus(exception));
            throw;
        }
        finally
        {
            try
            {
                journal.Record(TestPhase.Cleanup);
                await DisposeAsync();
                if (primaryFailure is null)
                    journal.Record(TestPhase.Completed);
            }
            catch (Exception cleanupFailure)
            {
                journal.Record(TestPhase.Failed, status: FailureStatus(cleanupFailure));
                if (primaryFailure is null)
                    throw;
                throw new AggregateException(
                    "The test body and resource cleanup both failed.",
                    primaryFailure,
                    cleanupFailure);
            }
        }
    }

    private static int? FailureStatus(Exception exception) => exception switch
    {
        HttpOperationException { Response: { } response } => (int)response.StatusCode,
        HttpRequestException { StatusCode: { } status } => (int)status,
        _ => null,
    };

    internal static async Task<Exception?> ExecuteCleanupAsync(
        CleanupAction cleanupAction,
        CancellationToken cancellationToken,
        TestTimings? timings = null)
    {
        using var timing = timings?.Measure(cleanupAction.TimingPhase);
        try
        {
            await cleanupAction.Cleanup(cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return new InvalidOperationException($"Cleanup action '{cleanupAction.Description}' failed.", exception);
        }
    }

    internal static void ValidateDescription(string description) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

    private bool TryRegister(CleanupAction cleanupAction)
    {
        lock (_lock)
        {
            if (_disposeStarted)
                return false;

            _cleanupActions.Add(cleanupAction);
            return true;
        }
    }

    private void ThrowIfDisposeStarted()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
        }
    }

    private async Task ThrowRegistrationRaceAsync(CleanupAction cleanupAction)
    {
        using var timeout = new CancellationTokenSource(_cleanupTimeout);
        var cleanupFailure = await ExecuteCleanupAsync(cleanupAction, timeout.Token, _timings);
        var disposed = new ObjectDisposedException(
            nameof(TestResourceScope),
            "The resource was created while its test resource scope was being disposed.");
        if (cleanupFailure is null)
            throw disposed;

        throw new AggregateException(
            "The resource scope was disposed during creation and emergency cleanup failed.",
            disposed,
            cleanupFailure);
    }

    internal sealed record CleanupAction(
        string Description,
        Func<CancellationToken, Task> Cleanup,
        string TimingPhase
    );
}

public sealed class TestResourceCleanupGroup
{
    private readonly List<TestResourceScope.CleanupAction> _cleanupActions = [];
    private readonly TimeSpan _cleanupTimeout;
    private readonly string _description;
    private readonly Lock _lock = new();
    private readonly int _maxConcurrency;
    private readonly TestTimings? _timings;
    private bool _cleanupStarted;

    internal TestResourceCleanupGroup(
        string description,
        TimeSpan cleanupTimeout,
        int maxConcurrency,
        TestTimings? timings)
    {
        TestResourceScope.ValidateDescription(description);
        _description = description;
        _cleanupTimeout = cleanupTimeout;
        _maxConcurrency = maxConcurrency;
        _timings = timings;
    }

    public async Task<T> CreateAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> create,
        Func<T, CancellationToken, Task> cleanup,
        CancellationToken cancellationToken = default,
        string timingPhase = "cleanup.action")
    {
        TestResourceScope.ValidateDescription(description);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(cleanup);
        ThrowIfCleanupStarted();

        var resource = await create(cancellationToken);
        var cleanupAction = new TestResourceScope.CleanupAction(
            description,
            token => cleanup(resource, token),
            timingPhase);
        if (TryRegister(cleanupAction))
            return resource;

        await ThrowRegistrationRaceAsync(cleanupAction);
        throw new UnreachableException();
    }

    // Timing phases are fixed code-owned names; descriptions may contain resource identifiers.
    public void Defer(
        string description,
        Func<CancellationToken, Task> cleanup,
        string timingPhase = "cleanup.action")
    {
        TestResourceScope.ValidateDescription(description);
        ArgumentNullException.ThrowIfNull(cleanup);

        if (!TryRegister(new TestResourceScope.CleanupAction(description, cleanup, timingPhase)))
            throw new ObjectDisposedException(nameof(TestResourceCleanupGroup));
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        TestResourceScope.CleanupAction[] cleanupActions;
        lock (_lock)
        {
            if (_cleanupStarted)
                return;

            _cleanupStarted = true;
            cleanupActions = _cleanupActions.ToArray();
        }

        Exception[] failures;
        if (_maxConcurrency > 1)
        {
            using var slots = new SemaphoreSlim(_maxConcurrency);
            failures = (await Task.WhenAll(
                    cleanupActions.Select(async action =>
                    {
                        // Pass an expired budget into queued actions too; earlier failures must not skip them.
                        await slots.WaitAsync();
                        try
                        {
                            return await TestResourceScope.ExecuteCleanupAsync(action, cancellationToken, _timings);
                        }
                        finally
                        {
                            slots.Release();
                        }
                    }))).OfType<Exception>()
                .ToArray();
        }
        else
        {
            var sequentialFailures = new List<Exception>();
            foreach (var cleanupAction in cleanupActions)
            {
                var failure = await TestResourceScope.ExecuteCleanupAsync(cleanupAction, cancellationToken, _timings);
                if (failure is not null)
                    sequentialFailures.Add(failure);
            }

            failures = sequentialFailures.ToArray();
        }

        if (failures.Length > 0)
            throw new AggregateException($"Cleanup group '{_description}' failed.", failures);
    }

    private bool TryRegister(TestResourceScope.CleanupAction cleanupAction)
    {
        lock (_lock)
        {
            if (_cleanupStarted)
                return false;

            _cleanupActions.Add(cleanupAction);
            return true;
        }
    }

    private void ThrowIfCleanupStarted()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_cleanupStarted, this);
        }
    }

    private async Task ThrowRegistrationRaceAsync(TestResourceScope.CleanupAction cleanupAction)
    {
        using var timeout = new CancellationTokenSource(_cleanupTimeout);
        var cleanupFailure = await TestResourceScope.ExecuteCleanupAsync(cleanupAction, timeout.Token, _timings);
        var disposed = new ObjectDisposedException(
            nameof(TestResourceCleanupGroup),
            "The resource was created while its cleanup group was running.");
        if (cleanupFailure is null)
            throw disposed;

        throw new AggregateException(
            "The cleanup group started during creation and emergency cleanup failed.",
            disposed,
            cleanupFailure);
    }
}
