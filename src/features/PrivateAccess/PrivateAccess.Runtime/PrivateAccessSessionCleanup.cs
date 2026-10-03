using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pole.Features.PrivateAccess.Contracts;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessSessionCleanup(
    LoadExpiredPrivateAccessSessionCleanupPage loadPage,
    MarkPrivateAccessSessionCleaned markCleaned,
    TimeProvider time,
    ILogger<PrivateAccessSessionCleanup> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sessions = await loadPage(time.GetUtcNow().UtcDateTime, 64, cancellationToken);
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await markCleaned(session.SessionId, timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Private Access session {SessionId} cleanup will retry after {FailureType}.",
                    session.SessionId, exception.GetType().Name);
            }
        }
    }
}

internal sealed class PrivateAccessSessionCleanupService(
    IServiceScopeFactory scopes,
    ILogger<PrivateAccessSessionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recovery remains enabled when new broker sessions are disabled. Persisted
        // namespace ownership and target references survive configuration changes.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await scope.ServiceProvider.GetRequiredService<PrivateAccessSessionCleanup>()
                    .RunAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Database errors can contain sensitive values. Record only the failure class.
                logger.LogWarning("Private Access cleanup will retry after {FailureType}.",
                    exception.GetType().Name);
            }
        }
    }
}
