using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pole.Features.Backups.Application.Configuration;

namespace Pole.Features.Backups.Application.Execution;

internal sealed class BackupWorker(
    IServiceScopeFactory scopes,
    ILogger<BackupWorker> log
) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<BackupProviders>();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatch = scope.ServiceProvider.GetRequiredService<BackupDispatcher>();
                var policy = await dispatch.PolicyAsync(stoppingToken);
                var operation = await dispatch.OperationAsync(stoppingToken);
                if (policy || operation)
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(
                    "Backup dispatch interrupted ({ExceptionType}); durable claims will retry",
                    ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
