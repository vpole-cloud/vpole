namespace Pole.Features.Functions.Runtime.Coordination;

public sealed class FunctionAdmissionLease : IAsyncDisposable
{
    private static readonly TimeSpan BestEffortReleaseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRenewalInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RenewalRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RenewalCallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(2);

    private readonly IFunctionAdmissionCoordinator coordinator;
    private readonly FunctionRevisionCoordinationKey key;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource renewalStop = new();
    private readonly CancellationTokenSource leaseLost = new();
    private readonly Task renewalTask;
    private FunctionAdmissionToken? token;
    private int disposed;

    public FunctionAdmissionLease(
        IFunctionAdmissionCoordinator coordinator,
        FunctionRevisionCoordinationKey key,
        FunctionAdmissionToken token,
        TimeProvider timeProvider)
    {
        this.coordinator = coordinator;
        this.key = key;
        this.token = token;
        this.timeProvider = timeProvider;
        renewalTask = RunRenewalAsync();
    }

    public FunctionAdmissionToken Token =>
        Volatile.Read(ref token) ?? throw new InvalidOperationException("The Function admission lease was released.");

    public CancellationToken LeaseLostToken => leaseLost.Token;

    public bool IsLost => leaseLost.IsCancellationRequested;

    public async Task<FunctionAdmissionReleaseDecision> ReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        var current = Interlocked.Exchange(ref token, null);
        if (current is null)
            return FunctionAdmissionReleaseDecision.AlreadyReleasedOrExpired;

        renewalStop.Cancel();
        try
        {
            await renewalTask.WaitAsync(cancellationToken);
            return await coordinator.ReleaseAsync(
                key,
                new FunctionAdmissionReleaseRequest(
                    current.AdmissionId,
                    current.LeaseId,
                    current.CoordinatorIncarnation),
                cancellationToken);
        }
        catch
        {
            Interlocked.CompareExchange(ref token, current, null);
            MarkLost();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            if (Volatile.Read(ref token) is not null)
            {
                using var timeout = new CancellationTokenSource(BestEffortReleaseTimeout, timeProvider);
                try
                {
                    await ReleaseAsync(timeout.Token);
                }
                catch
                {
                    // The grain lease has a TTL. Cleanup is deliberately best effort so an unavailable
                    // coordination cluster cannot hold the HTTP response or dispatcher shutdown open.
                }
            }
        }
        finally
        {
            renewalStop.Cancel();
            try
            {
                await renewalTask;
            }
            catch
            {
                // A lost lease is surfaced through LeaseLostToken while the caller owns it.
            }
            renewalStop.Dispose();
            leaseLost.Dispose();
        }
    }

    private async Task RunRenewalAsync()
    {
        var retrying = false;
        try
        {
            while (!renewalStop.IsCancellationRequested)
            {
                var current = Volatile.Read(ref token);
                if (current is null)
                    return;

                var remaining = current.ExpiresAtUtc - timeProvider.GetUtcNow();
                if (remaining <= ExpirySafetyMargin)
                {
                    MarkLost();
                    return;
                }

                var renewalWindow = remaining - ExpirySafetyMargin;
                if (renewalWindow <= TimeSpan.FromMilliseconds(100))
                {
                    MarkLost();
                    return;
                }

                var delay = retrying
                    ? TimeSpan.FromTicks(Math.Min(
                        RenewalRetryInterval.Ticks,
                        renewalWindow.Ticks / 2))
                    : TimeSpan.FromTicks(Math.Min(
                        MaximumRenewalInterval.Ticks,
                        Math.Min(remaining.Ticks / 3, renewalWindow.Ticks / 2)));
                await Task.Delay(delay, timeProvider, renewalStop.Token);

                current = Volatile.Read(ref token);
                if (current is null)
                    return;
                remaining = current.ExpiresAtUtc - timeProvider.GetUtcNow();
                var callWindow = TimeSpan.FromTicks(Math.Min(
                    RenewalCallTimeout.Ticks,
                    remaining.Ticks - ExpirySafetyMargin.Ticks));
                if (callWindow <= TimeSpan.Zero)
                {
                    MarkLost();
                    return;
                }

                try
                {
                    using var callTimeout = new CancellationTokenSource(callWindow, timeProvider);
                    using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        renewalStop.Token,
                        callTimeout.Token);
                    var decision = await coordinator.RenewAsync(
                        key,
                        new FunctionAdmissionRenewalRequest(current),
                        callCancellation.Token);
                    if (decision.Kind != FunctionAdmissionRenewalDecisionKind.Renewed ||
                        decision.Token is not { } renewed ||
                        !IsSameLease(current, renewed) ||
                        renewed.ExpiresAtUtc <= timeProvider.GetUtcNow() + ExpirySafetyMargin)
                    {
                        MarkLost();
                        return;
                    }

                    Interlocked.CompareExchange(ref token, renewed, current);
                    retrying = false;
                }
                catch (OperationCanceledException) when (renewalStop.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    retrying = true;
                    if (current.ExpiresAtUtc - timeProvider.GetUtcNow() <= ExpirySafetyMargin)
                    {
                        MarkLost();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (renewalStop.IsCancellationRequested)
        {
            // Normal release/disposal path.
        }
        catch
        {
            MarkLost();
        }
    }

    private static bool IsSameLease(FunctionAdmissionToken first, FunctionAdmissionToken second) =>
        string.Equals(first.AdmissionId, second.AdmissionId, StringComparison.Ordinal) &&
        string.Equals(first.LeaseId, second.LeaseId, StringComparison.Ordinal) &&
        string.Equals(first.CoordinatorIncarnation, second.CoordinatorIncarnation, StringComparison.Ordinal);

    private void MarkLost()
    {
        if (!leaseLost.IsCancellationRequested)
            leaseLost.Cancel();
    }
}
