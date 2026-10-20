using System.Diagnostics;

namespace Pole.Testing;

public sealed class TestResourceScope : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly List<CleanupAction> _cleanupActions = [];
    private readonly string _description;
    private readonly TimeSpan _cleanupTimeout;
    private bool _disposeStarted;

    public TestResourceScope(string description, TimeSpan? cleanupTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (cleanupTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout), "Cleanup timeout must be positive.");

        _description = description;
        _cleanupTimeout = cleanupTimeout ?? TimeSpan.FromMinutes(3);
    }

    public async Task<T> CreateAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> create,
        Func<T, CancellationToken, Task> cleanup,
        CancellationToken cancellationToken = default)
    {
        ValidateDescription(description);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(cleanup);
        ThrowIfDisposeStarted();

        var resource = await create(cancellationToken);
        var cleanupAction = new CleanupAction(description, token => cleanup(resource, token));
        if (TryRegister(cleanupAction))
            return resource;

        await ThrowRegistrationRaceAsync(cleanupAction);
        throw new UnreachableException();
    }

    public void Defer(string description, Func<CancellationToken, Task> cleanup)
    {
        ValidateDescription(description);
        ArgumentNullException.ThrowIfNull(cleanup);

        if (!TryRegister(new CleanupAction(description, cleanup)))
            throw new ObjectDisposedException(nameof(TestResourceScope));
    }

    public TestResourceCleanupGroup CreateConcurrentCleanupGroup(string description)
    {
        var group = new TestResourceCleanupGroup(description, _cleanupTimeout, concurrent: true);
        Defer(description, group.CleanupAsync);
        return group;
    }

    public TestResourceCleanupGroup CreateCleanupSequence(string description)
    {
        var group = new TestResourceCleanupGroup(description, _cleanupTimeout, concurrent: false);
        Defer(description, group.CleanupAsync);
        return group;
    }

    public async Task RunAsync(Func<Task> body)
    {
        ThrowIfDisposeStarted();
        Exception? primaryFailure = null;
        try
        {
            await body();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                throw new AggregateException(
                    "The test body and resource cleanup both failed.",
                    primaryFailure,
                    cleanupFailure);
            }
        }
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
        var failures = new List<Exception>();
        foreach (var cleanupAction in cleanupActions)
        {
            var failure = await ExecuteCleanupAsync(cleanupAction, timeout.Token);
            if (failure is not null)
                failures.Add(failure);
        }

        if (failures.Count > 0)
            throw new AggregateException($"Cleanup for '{_description}' failed.", failures);
    }

    internal static async Task<Exception?> ExecuteCleanupAsync(
        CleanupAction cleanupAction,
        CancellationToken cancellationToken)
    {
        try
        {
            await cleanupAction.Cleanup(cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return new InvalidOperationException(
                $"Cleanup action '{cleanupAction.Description}' failed.",
                exception);
        }
    }

    internal sealed record CleanupAction(
        string Description,
        Func<CancellationToken, Task> Cleanup);

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
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
    }

    private async Task ThrowRegistrationRaceAsync(CleanupAction cleanupAction)
    {
        using var timeout = new CancellationTokenSource(_cleanupTimeout);
        var cleanupFailure = await ExecuteCleanupAsync(cleanupAction, timeout.Token);
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
}

public sealed class TestResourceCleanupGroup
{
    private readonly Lock _lock = new();
    private readonly List<TestResourceScope.CleanupAction> _cleanupActions = [];
    private readonly string _description;
    private readonly TimeSpan _cleanupTimeout;
    private readonly bool _concurrent;
    private bool _cleanupStarted;

    internal TestResourceCleanupGroup(
        string description,
        TimeSpan cleanupTimeout,
        bool concurrent)
    {
        TestResourceScope.ValidateDescription(description);
        _description = description;
        _cleanupTimeout = cleanupTimeout;
        _concurrent = concurrent;
    }

    public async Task<T> CreateAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> create,
        Func<T, CancellationToken, Task> cleanup,
        CancellationToken cancellationToken = default)
    {
        TestResourceScope.ValidateDescription(description);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(cleanup);
        ThrowIfCleanupStarted();

        var resource = await create(cancellationToken);
        var cleanupAction = new TestResourceScope.CleanupAction(
            description,
            token => cleanup(resource, token));
        if (TryRegister(cleanupAction))
            return resource;

        await ThrowRegistrationRaceAsync(cleanupAction);
        throw new UnreachableException();
    }

    public void Defer(string description, Func<CancellationToken, Task> cleanup)
    {
        TestResourceScope.ValidateDescription(description);
        ArgumentNullException.ThrowIfNull(cleanup);

        if (!TryRegister(new TestResourceScope.CleanupAction(description, cleanup)))
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
        if (_concurrent)
        {
            failures = (await Task.WhenAll(cleanupActions.Select(action =>
                    TestResourceScope.ExecuteCleanupAsync(action, cancellationToken))))
                .OfType<Exception>()
                .ToArray();
        }
        else
        {
            var sequentialFailures = new List<Exception>();
            foreach (var cleanupAction in cleanupActions)
            {
                var failure = await TestResourceScope.ExecuteCleanupAsync(
                    cleanupAction,
                    cancellationToken);
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
            ObjectDisposedException.ThrowIf(_cleanupStarted, this);
    }

    private async Task ThrowRegistrationRaceAsync(TestResourceScope.CleanupAction cleanupAction)
    {
        using var timeout = new CancellationTokenSource(_cleanupTimeout);
        var cleanupFailure = await TestResourceScope.ExecuteCleanupAsync(cleanupAction, timeout.Token);
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
