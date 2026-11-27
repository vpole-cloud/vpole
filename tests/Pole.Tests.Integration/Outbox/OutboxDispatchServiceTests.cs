// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Features.Outbox.Application;
using Xunit;

namespace Pole.Tests.Integration.Outbox;

public sealed class OutboxDispatchServiceTests
{
    [Fact]
    public async Task DisabledInterval_DoesNotDispatch()
    {
        var trigger = new RecordingTrigger((_, _) => Task.CompletedTask);
        using var services = CreateServices(trigger);
        using var service = CreateService(services, interval: null);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, trigger.Count);
    }

    [Fact]
    public async Task SlowDispatch_CoalescesTicksAndDoesNotOverlap()
    {
        var firstStarted = Signal();
        var releaseFirst = Signal();
        var secondStarted = Signal();
        var trigger = new RecordingTrigger(async (count, cancellationToken) =>
        {
            if (count == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        });
        using var services = CreateServices(trigger);
        using var service = CreateService(services, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(80));
            Assert.Equal(1, trigger.Count);
            releaseFirst.SetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, trigger.Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DispatchFailure_DoesNotStopSubsequentTicks()
    {
        var recovered = Signal();
        var trigger = new RecordingTrigger((count, _) =>
        {
            if (count == 1)
                throw new InvalidOperationException("temporary storage outage");

            recovered.TrySetResult();
            return Task.CompletedTask;
        });
        using var services = CreateServices(trigger);
        using var service = CreateService(services, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(trigger.Count >= 2);
    }

    [Fact]
    public async Task Shutdown_CancelsTheActiveDispatch()
    {
        var started = Signal();
        var cancelled = Signal();
        var trigger = new RecordingTrigger(async (_, cancellationToken) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancelled.SetResult();
            }
        });
        using var services = CreateServices(trigger);
        using var service = CreateService(services, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(1, trigger.Count);
    }

    private static OutboxProcessingTriggerService CreateService(IServiceProvider services, TimeSpan? interval) => new(
        Options.Create(new OutboxSettings { ProcessingTriggerInterval = interval }),
        services.GetRequiredService<IServiceScopeFactory>(),
        TimeProvider.System,
        NullLogger<OutboxProcessingTriggerService>.Instance);

    private static ServiceProvider CreateServices(IOutboxProcessingTrigger trigger) =>
        new ServiceCollection().AddSingleton(trigger).BuildServiceProvider();

    [Fact]
    public async Task DisabledInterval_DoesNotRequireHangfireStorage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMongoDatabase>(_ =>
            throw new InvalidOperationException("Disabled dispatch must not read MongoDB."));
        services.AddSingleton<IOutboxPublisher, DisabledPublisher>();
        services.AddOutboxFeature(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        var service = Assert.IsType<OutboxProcessingTriggerService>(
            Assert.Single(provider.GetServices<IHostedService>()));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DisabledPublisher : IOutboxPublisher
    {
        public Task PublishAsync(
            object message,
            Type messageType,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Disabled dispatch must not publish.");
    }

    private sealed class RecordingTrigger(
        Func<int, CancellationToken, Task> execute
    ) : IOutboxProcessingTrigger
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public Task TriggerAsync(CancellationToken cancellationToken) => execute(
            Interlocked.Increment(ref _count),
            cancellationToken);
    }
}
