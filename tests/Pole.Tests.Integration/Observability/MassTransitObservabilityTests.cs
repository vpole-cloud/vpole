using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pole.Observability;
using Xunit;

namespace Pole.Tests.Integration.Observability;

public sealed class MassTransitObservabilityTests
{
    private const string TraceId = "fedcba98765432100123456789abcdef";
    private const string CorrelationId = "bus-correlation-from-test";

    [Fact]
    public async Task PublishAndConsume_PropagatesTraceContextThroughHeaders()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<ConsumedTraceContext>();
        services.AddMassTransit(configurator =>
        {
            configurator.AddConsumer<TestMessageConsumer>();
            configurator.UsingInMemory((context, cfg) =>
            {
                cfg.UsePublishFilter(typeof(TraceContextPublishFilter<>), context);
                cfg.UseConsumeFilter(typeof(TraceContextConsumeFilter<>), context);
                cfg.ConfigureEndpoints(context);
            });
        });

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync();

        try
        {
            using var scope = TraceContext.Push(new TraceContextSnapshot(TraceId, CorrelationId));
            await using var serviceScope = provider.CreateAsyncScope();
            await serviceScope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(new TestMessage("hello"));

            var consumed = await provider.GetRequiredService<ConsumedTraceContext>().WaitAsync();

            Assert.Equal(TraceId, consumed.TraceId);
            Assert.Equal(CorrelationId, consumed.CorrelationId);
        }
        finally
        {
            await bus.StopAsync();
        }
    }

    [Fact]
    public async Task PublishFromConsumer_PropagatesTraceContextToDerivedMessages()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<DerivedTraceContext>();
        services.AddMassTransit(configurator =>
        {
            configurator.AddConsumer<SourceMessageConsumer>();
            configurator.AddConsumer<DerivedMessageConsumer>();
            configurator.UsingInMemory((context, cfg) =>
            {
                cfg.UsePublishFilter(typeof(TraceContextPublishFilter<>), context);
                cfg.UseConsumeFilter(typeof(TraceContextConsumeFilter<>), context);
                cfg.ConfigureEndpoints(context);
            });
        });

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync();

        try
        {
            using var scope = TraceContext.Push(new TraceContextSnapshot(TraceId, CorrelationId));
            await using var serviceScope = provider.CreateAsyncScope();
            await serviceScope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(new SourceMessage("hello"));

            var consumed = await provider.GetRequiredService<DerivedTraceContext>().WaitAsync();

            Assert.Equal(TraceId, consumed.TraceId);
            Assert.Equal(CorrelationId, consumed.CorrelationId);
        }
        finally
        {
            await bus.StopAsync();
        }
    }

    private sealed record TestMessage(string Value);

    private sealed record SourceMessage(string Value);

    private sealed record DerivedMessage(string Value);

    private sealed class TestMessageConsumer(ConsumedTraceContext consumedTraceContext)
        : IConsumer<TestMessage>
    {
        public Task Consume(ConsumeContext<TestMessage> context)
        {
            consumedTraceContext.Set(TraceContext.Current);
            return Task.CompletedTask;
        }
    }

    private sealed class SourceMessageConsumer : IConsumer<SourceMessage>
    {
        public Task Consume(ConsumeContext<SourceMessage> context)
            => context.Publish(new DerivedMessage(context.Message.Value));
    }

    private sealed class DerivedMessageConsumer(DerivedTraceContext derivedTraceContext)
        : IConsumer<DerivedMessage>
    {
        public Task Consume(ConsumeContext<DerivedMessage> context)
        {
            derivedTraceContext.Set(TraceContext.Current);
            return Task.CompletedTask;
        }
    }

    private sealed class ConsumedTraceContext : TraceContextCapture
    {
    }

    private sealed class DerivedTraceContext : TraceContextCapture
    {
    }

    private abstract class TraceContextCapture
    {
        private readonly TaskCompletionSource<TraceContextSnapshot> _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Set(TraceContextSnapshot? traceContext)
        {
            if (traceContext is not null)
            {
                _source.TrySetResult(traceContext);
            }
        }

        public async Task<TraceContextSnapshot> WaitAsync()
        {
            var completed = await Task.WhenAny(_source.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(_source.Task, completed);
            return await _source.Task;
        }
    }
}
