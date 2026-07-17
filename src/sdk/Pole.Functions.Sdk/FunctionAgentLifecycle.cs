using Microsoft.Extensions.Hosting;

namespace Pole.Functions.Sdk;

internal sealed class FunctionAgentIdentity
{
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
}

internal sealed class FunctionAgentLifecycle
{
    private int draining;

    public bool IsDraining => Volatile.Read(ref draining) != 0;

    public void BeginDrain() => Interlocked.Exchange(ref draining, 1);
}

internal sealed class FunctionAgentLifecycleService(
    FunctionAgentLifecycle lifecycle,
    FunctionHandlerRegistry registry,
    FunctionExecutionTracker executionTracker,
    IHostApplicationLifetime applicationLifetime)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = registry.All;
        applicationLifetime.ApplicationStopping.Register(BeginDrain);
        return Task.CompletedTask;

        void BeginDrain()
        {
            lifecycle.BeginDrain();
            _ = executionTracker.BeginDrain();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lifecycle.BeginDrain();
        await executionTracker.BeginDrain().WaitAsync(cancellationToken);
    }
}
