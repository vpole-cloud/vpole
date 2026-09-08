using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Application;
using Xunit;
using EventJson = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Tests.Integration.Events;

public sealed class EventIdentityContractTests
{
    [Fact]
    public void Catalog_RegistersEachVersionAsADistinctEventIdentity()
    {
        var catalog = new OutboxEventAssemblyCatalog(
        [
            new SelectedTypesAssembly(
                "event-identity-versions",
                [typeof(VersionOneEvent), typeof(VersionTwoEvent)])
        ]);

        Assert.Same(typeof(VersionOneEvent), catalog.GetRequiredEventType("pole.test/v1/example"));
        Assert.Same(typeof(VersionTwoEvent), catalog.GetRequiredEventType("pole.test/v2/example"));
    }

    [Fact]
    public void Catalog_RejectsDuplicateVersionedIdentity()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new OutboxEventAssemblyCatalog(
        [
            new SelectedTypesAssembly(
                "event-identity-duplicates",
                [typeof(DuplicateVersionOneEvent), typeof(VersionOneEvent)])
        ]));

        Assert.Contains("pole.test/v1/example", exception.Message);
    }

    [Fact]
    public void EventTypeIdentity_RequiresAPositiveVersion()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EventTypeIdentity.GetRequiredName(typeof(ZeroVersionEvent)));

        Assert.Contains("namespace/vN/name", exception.Message);
    }

    [Fact]
    public void VersionedEvent_RoundTripsItsDurablePayload()
    {
        var @event = new VersionOneEvent("value")
        {
            EventCore = EventCore.Create("tenant") with
            {
                TraceId = "0123456789abcdef0123456789abcdef",
                CorrelationId = "event-identity-roundtrip"
            }
        };

        var payload = JsonSerializer.Serialize(@event, EventJson.SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<VersionOneEvent>(payload, EventJson.SerializerOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(@event.Value, roundTripped.Value);
        Assert.Equal(@event.EventCore, roundTripped.EventCore);
    }

    [Fact]
    public async Task RabbitMq_PublishesAndConsumesTheVersionedEvent()
    {
        await using var container = new ContainerBuilder("rabbitmq:4.1-management-alpine")
            .WithPortBinding(5672, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5672))
            .Build();
        await container.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton<ConsumedVersionedEvent>();
        services.AddMassTransit(configurator =>
        {
            configurator.AddConsumer<VersionOneEventConsumer>();
            configurator.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(
                    "127.0.0.1",
                    checked((ushort)container.GetMappedPublicPort(5672)),
                    "/",
                    host =>
                    {
                        host.Username("guest");
                        host.Password("guest");
                    });
                cfg.ReceiveEndpoint(
                    "event-identity-contract-tests",
                    endpoint => endpoint.ConfigureConsumer<VersionOneEventConsumer>(context));
            });
        });

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync();
        try
        {
            var @event = new VersionOneEvent("published")
            {
                EventCore = EventCore.Create("tenant")
            };
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(@event);

            var consumed = await provider.GetRequiredService<ConsumedVersionedEvent>().WaitAsync();
            Assert.Equal(@event, consumed);
        }
        finally
        {
            await bus.StopAsync();
        }
    }

    [EntityName("pole.test/v1/example")]
    public sealed record VersionOneEvent(string Value) : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    [EntityName("pole.test/v2/example")]
    private sealed record VersionTwoEvent(string Value) : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    [EntityName("pole.test/v1/example")]
    private sealed record DuplicateVersionOneEvent(string Value) : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    [EntityName("pole.test/v0/example")]
    private sealed record ZeroVersionEvent(string Value) : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    public sealed class VersionOneEventConsumer(ConsumedVersionedEvent consumed) : IConsumer<VersionOneEvent>
    {
        public Task Consume(ConsumeContext<VersionOneEvent> context)
        {
            consumed.Set(context.Message);
            return Task.CompletedTask;
        }
    }

    public sealed class ConsumedVersionedEvent
    {
        private readonly TaskCompletionSource<VersionOneEvent> _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Set(VersionOneEvent @event) => _source.TrySetResult(@event);

        public async Task<VersionOneEvent> WaitAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await _source.Task.WaitAsync(timeout.Token);
        }
    }

    private sealed class SelectedTypesAssembly(string name, Type[] types) : System.Reflection.Assembly
    {
        public override string FullName => name;
        public override bool IsDynamic => false;
        public override Type[] GetTypes() => types;
    }
}
