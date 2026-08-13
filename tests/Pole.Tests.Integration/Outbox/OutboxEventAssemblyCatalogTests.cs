using System.Reflection;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Application;
using Xunit;

namespace Pole.Tests.Integration.Outbox;

public sealed class OutboxEventAssemblyCatalogTests
{
    [Fact]
    public void ProductionEventAssemblies_BuildOneValidCatalog()
    {
        var catalog = new OutboxEventAssemblyCatalog(
        [
            typeof(Pole.Features.WebServices.Events.WebServiceCreatedEvent).Assembly,
            typeof(Pole.Features.Functions.Events.FunctionAppCreatedEvent).Assembly,
            typeof(Pole.Features.Postgres.Events.PostgresClusterCreatedEvent).Assembly,
            typeof(Pole.Features.RedisServices.Events.RedisServiceCreatedEvent).Assembly,
            typeof(Pole.Features.ResourceDeployments.Events.ResourceDeploymentAppliedEvent).Assembly,
            typeof(Pole.Features.ResourceGroups.Events.ResourceGroupCreatedEvent).Assembly,
            typeof(Pole.Features.SourceBuilds.Events.SourceBuildRequestedEvent).Assembly,
            typeof(Pole.Features.Users.Events.UserCreatedEvent).Assembly
        ]);

        Assert.Contains("web-service-created", catalog.EventTypes.Keys);
        Assert.Contains("function-app-created", catalog.EventTypes.Keys);
        Assert.Contains("postgres-cluster-created", catalog.EventTypes.Keys);
        Assert.Contains("redis-service-created", catalog.EventTypes.Keys);
        Assert.Contains("resource-deployment-applied", catalog.EventTypes.Keys);
        Assert.Contains("runtime-cell-reconciliation-requested", catalog.EventTypes.Keys);
        Assert.Contains("source-build-requested", catalog.EventTypes.Keys);
        Assert.Contains("user-created", catalog.EventTypes.Keys);
    }

    [Fact]
    public void Constructor_BuildsImmutableMap_ForConcreteNamedEvents()
    {
        var assembly = new SelectedTypesAssembly(
            "catalog-valid",
            [typeof(FirstNamedEvent), typeof(SecondNamedEvent), typeof(AbstractEvent)]);

        var catalog = new OutboxEventAssemblyCatalog([assembly]);

        Assert.Equal(2, catalog.EventTypes.Count);
        Assert.Same(
            typeof(FirstNamedEvent),
            catalog.GetRequiredEventType("catalog-first"));
        Assert.Same(
            typeof(SecondNamedEvent),
            catalog.GetRequiredEventType("catalog-second"));
        Assert.Equal(
            "catalog-first",
            EventTypeIdentity.GetRequiredName(typeof(FirstNamedEvent)));
    }

    [Fact]
    public void Constructor_RejectsConcreteEventWithoutPersistedName()
    {
        var assembly = new SelectedTypesAssembly(
            "catalog-missing-name",
            [typeof(MissingEntityNameEvent)]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new OutboxEventAssemblyCatalog([assembly]));

        Assert.Contains(nameof(MissingEntityNameEvent), exception.Message);
        Assert.Contains(nameof(EntityNameAttribute), exception.Message);
    }

    [Fact]
    public void EventTypeIdentity_RejectsNonConcreteEventTypes()
    {
        var abstractException = Assert.Throws<ArgumentException>(
            () => EventTypeIdentity.GetRequiredName(typeof(AbstractEvent)));
        var interfaceException = Assert.Throws<ArgumentException>(
            () => EventTypeIdentity.GetRequiredName(typeof(IPoleEvent)));

        Assert.Contains("concrete class", abstractException.Message);
        Assert.Contains("concrete class", interfaceException.Message);
    }

    [Fact]
    public void Constructor_RejectsWhitespacePersistedName()
    {
        var assembly = new SelectedTypesAssembly(
            "catalog-whitespace-name",
            [typeof(WhitespaceEntityNameEvent)]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new OutboxEventAssemblyCatalog([assembly]));

        Assert.Contains(nameof(WhitespaceEntityNameEvent), exception.Message);
    }

    [Fact]
    public void EventTypeIdentity_NormalizesPersistedNameWhitespace()
    {
        Assert.Equal(
            "catalog-padded",
            EventTypeIdentity.GetRequiredName(typeof(PaddedEntityNameEvent)));
    }

    [Fact]
    public void Constructor_RejectsDuplicateNamesDeterministically()
    {
        var firstOrder = Assert.Throws<InvalidOperationException>(
            () => new OutboxEventAssemblyCatalog(
            [
                new SelectedTypesAssembly(
                    "duplicates-first",
                    [typeof(DuplicateEventB), typeof(DuplicateEventA)])
            ]));
        var secondOrder = Assert.Throws<InvalidOperationException>(
            () => new OutboxEventAssemblyCatalog(
            [
                new SelectedTypesAssembly(
                    "duplicates-second",
                    [typeof(DuplicateEventA), typeof(DuplicateEventB)])
            ]));

        Assert.Equal(firstOrder.Message, secondOrder.Message);
        Assert.Contains("catalog-duplicate", firstOrder.Message);
        Assert.True(
            firstOrder.Message.IndexOf(
                nameof(DuplicateEventA),
                StringComparison.Ordinal) <
            firstOrder.Message.IndexOf(
                nameof(DuplicateEventB),
                StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_UsesLoadableTypes_FromReflectionTypeLoadException()
    {
        var assembly = new SelectedTypesAssembly(
            "catalog-partially-loadable",
            [typeof(FirstNamedEvent)],
            throwTypeLoadException: true);

        var catalog = new OutboxEventAssemblyCatalog([assembly]);

        Assert.Same(
            typeof(FirstNamedEvent),
            catalog.GetRequiredEventType("catalog-first"));
    }

    [Fact]
    public void Constructor_StillValidatesLoadableTypes_AfterReflectionTypeLoadException()
    {
        var assembly = new SelectedTypesAssembly(
            "catalog-partially-loadable-invalid",
            [typeof(MissingEntityNameEvent)],
            throwTypeLoadException: true);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new OutboxEventAssemblyCatalog([assembly]));

        Assert.Contains(nameof(MissingEntityNameEvent), exception.Message);
    }

    [Fact]
    public void GetRequiredEventType_RejectsUnknownPersistedName()
    {
        var catalog = new OutboxEventAssemblyCatalog(
        [
            new SelectedTypesAssembly("catalog-lookup", [typeof(FirstNamedEvent)])
        ]);

        var exception = Assert.Throws<KeyNotFoundException>(
            () => catalog.GetRequiredEventType("catalog-unknown"));

        Assert.Contains("catalog-unknown", exception.Message);
        Assert.Contains("catalog-first", exception.Message);
    }

    [EntityName("catalog-first")]
    private sealed record FirstNamedEvent : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    [EntityName("catalog-second")]
    private sealed record SecondNamedEvent : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    private abstract record AbstractEvent : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    private sealed record MissingEntityNameEvent : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    [EntityName(" ")]
    private sealed record WhitespaceEntityNameEvent : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    [EntityName(" catalog-padded ")]
    private sealed record PaddedEntityNameEvent : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    [EntityName("catalog-duplicate")]
    private sealed record DuplicateEventA : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    [EntityName("catalog-duplicate")]
    private sealed record DuplicateEventB : IPoleEvent
    {
        public EventCore EventCore { get; init; } = EventCore.Create();
    }

    private sealed class SelectedTypesAssembly(
        string fullName,
        Type[] types,
        bool throwTypeLoadException = false)
        : Assembly
    {
        public override string FullName => fullName;
        public override bool IsDynamic => false;

        public override Type[] GetTypes()
        {
            if (!throwTypeLoadException)
                return types;

            throw new ReflectionTypeLoadException(
                [.. types, null],
                [new FileNotFoundException("A test-only dependency could not be loaded.")]);
        }
    }
}
