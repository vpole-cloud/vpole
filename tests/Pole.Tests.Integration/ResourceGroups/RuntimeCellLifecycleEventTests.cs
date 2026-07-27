using System.Reflection;
using System.Text.Json;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Events;
using Xunit;
using EventJson = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class RuntimeCellLifecycleEventTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string RuntimeCellId = "507f1f77bcf86cd799439012";
    private const long Generation = 7;
    private static readonly EventCore Core = EventCore.Create(TenantId) with
    {
        OccurredOn = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc),
        TraceId = "0123456789abcdef0123456789abcdef",
        CorrelationId = "runtime-cell-event-test"
    };

    public static TheoryData<Type, IPoleEvent, string> Events =>
        new()
        {
            {
                typeof(RuntimeCellReconciliationRequestedEvent),
                new RuntimeCellReconciliationRequestedEvent(RuntimeCellId, Generation) { EventCore = Core },
                "runtime-cell-reconciliation-requested"
            },
            {
                typeof(RuntimeCellReadyEvent),
                new RuntimeCellReadyEvent(RuntimeCellId, Generation) { EventCore = Core },
                "runtime-cell-ready"
            },
            {
                typeof(RuntimeCellReconciliationFailedEvent),
                new RuntimeCellReconciliationFailedEvent(RuntimeCellId, Generation, "NamespaceOwnershipConflict") { EventCore = Core },
                "runtime-cell-reconciliation-failed"
            },
            {
                typeof(RuntimeCellDeletionRequestedEvent),
                new RuntimeCellDeletionRequestedEvent(RuntimeCellId, Generation) { EventCore = Core },
                "runtime-cell-deletion-requested"
            },
            {
                typeof(RuntimeCellDeletedEvent),
                new RuntimeCellDeletedEvent(RuntimeCellId, Generation) { EventCore = Core },
                "runtime-cell-deleted"
            }
        };

    [Theory]
    [MemberData(nameof(Events))]
    public void Events_HaveStableEntityNames_AndRoundTripOnlyDurableMetadata(
        Type eventType,
        IPoleEvent @event,
        string expectedEntityName)
    {
        var entityName = eventType.GetCustomAttribute<EntityNameAttribute>();
        Assert.NotNull(entityName);
        Assert.Equal(expectedEntityName, entityName.EntityName);

        var payloadJson = JsonSerializer.Serialize(@event, eventType, EventJson.SerializerOptions);
        using var payload = JsonDocument.Parse(payloadJson);
        var root = payload.RootElement;

        Assert.Equal(RuntimeCellId, root.GetProperty("runtimeCellId").GetString());
        Assert.Equal(Generation, root.GetProperty("generation").GetInt64());
        Assert.Equal(TenantId, root.GetProperty("eventCore").GetProperty("tenantId").GetString());
        Assert.Equal(Core.TraceId, root.GetProperty("eventCore").GetProperty("traceId").GetString());
        Assert.Equal(Core.CorrelationId, root.GetProperty("eventCore").GetProperty("correlationId").GetString());
        Assert.False(root.TryGetProperty("deploymentTargetId", out _));
        Assert.False(root.TryGetProperty("kubernetesNamespace", out _));
        Assert.False(root.TryGetProperty("failureMessage", out _));
        Assert.False(root.TryGetProperty("credentials", out _));

        var roundTripped = JsonSerializer.Deserialize(payloadJson, eventType, EventJson.SerializerOptions);
        var durableEvent = Assert.IsAssignableFrom<IPoleEvent>(roundTripped);
        Assert.Equal(TenantId, durableEvent.EventCore.TenantId);
    }

    [Fact]
    public void ReconciliationFailure_ContainsOnlyTheSafeFailureCode()
    {
        var @event = new RuntimeCellReconciliationFailedEvent(
            RuntimeCellId,
            Generation,
            "NamespaceOwnershipConflict")
        {
            EventCore = Core
        };

        var payloadJson = JsonSerializer.Serialize(@event, EventJson.SerializerOptions);
        using var payload = JsonDocument.Parse(payloadJson);
        var root = payload.RootElement;

        Assert.Equal("NamespaceOwnershipConflict", root.GetProperty("failureCode").GetString());
        Assert.Equal(4, root.EnumerateObject().Count());
    }

    [Fact]
    public void IsolationScopeDeletionRequested_HasStableNameAndTenantMetadata()
    {
        var @event = new IsolationScopeDeletionRequestedEvent(
            "507f1f77bcf86cd799439013",
            "507f1f77bcf86cd799439014")
        {
            EventCore = Core
        };
        var entityName = typeof(IsolationScopeDeletionRequestedEvent)
            .GetCustomAttribute<EntityNameAttribute>();

        Assert.NotNull(entityName);
        Assert.Equal("isolation-scope-deletion-requested", entityName.EntityName);
        var payloadJson = JsonSerializer.Serialize(@event, EventJson.SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<IsolationScopeDeletionRequestedEvent>(
            payloadJson,
            EventJson.SerializerOptions);
        Assert.NotNull(roundTripped);
        Assert.Equal(TenantId, roundTripped.EventCore.TenantId);
        Assert.Equal(@event.IsolationScopeId, roundTripped.IsolationScopeId);
        Assert.Equal(@event.ResourceGroupId, roundTripped.ResourceGroupId);
    }
}
