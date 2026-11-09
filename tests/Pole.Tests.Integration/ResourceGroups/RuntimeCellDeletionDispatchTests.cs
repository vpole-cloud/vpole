using System.Reflection;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Consumers;
using Pole.Features.ResourceGroups.Application.Jobs;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Platform.Hosting;
using Xunit;
using EventJson = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class RuntimeCellDeletionDispatchTests
{
    [Fact]
    public void LegacyDeletingDocumentAndEvent_DefaultToInitialCheck()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var cell = RuntimeCellFactory.Create(ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), "legacy-cell",
            new RuntimeCellFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now)).RequestDeletion(now);
        var document = cell.ToBsonDocument();
        document["State"].AsBsonDocument.Remove("NextCheckAtUtc");
        document["State"].AsBsonDocument.Remove("CheckSequence");

        var legacy = BsonSerializer.Deserialize<RuntimeCell>(document);
        var state = Assert.IsType<DeletingRuntimeCellState>(legacy.State);
        Assert.Equal(0, state.CheckSequence);
        Assert.Null(state.NextCheckAtUtc);
        var request = JsonSerializer.Deserialize<RuntimeCellDeletionRequestedEvent>(
            """{"runtimeCellId":"legacy-cell","generation":2,"eventCore":{"tenantId":"tenant"}}""", EventJson.SerializerOptions)!;
        Assert.Equal("tenant", request.EventCore.TenantId);
        Assert.Equal(0, request.CheckSequence);
        Assert.Null(request.NotBeforeUtc);
    }

    [Fact]
    public async Task ScheduledMessage_RetainsDueSequenceAndTenant_AndSchedulingFailurePropagates()
    {
        var due = DateTime.UtcNow.AddSeconds(5);
        var request = new RuntimeCellDeletionRequestedEvent("cell", 7, due, 3) { EventCore = EventCore.Create("tenant") };
        var scheduled = new List<(RuntimeCellDeletionRequestedEvent Request, DateTime Due)>();
        var consumer = new DeleteRuntimeCellOnDeletionRequestedConsumer(null!, (message, at) => scheduled.Add((message, at)));

        await consumer.Consume(Context(request));

        Assert.Equal((request, due), Assert.Single(scheduled));
        var failure = new InvalidOperationException("Controlled scheduler failure.");
        var failing = new DeleteRuntimeCellOnDeletionRequestedConsumer(null!, (_, _) => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Consume(Context(request))));
    }

    [Fact]
    public void Registration_SchedulesTheProviderJobWithTheDurableRequest()
    {
        var jobs = new RecordingJobs();
        using var provider = new ServiceCollection().AddSingleton<IBackgroundJobClient>(jobs)
            .AddResourceGroupsFeature(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var due = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var request = new RuntimeCellDeletionRequestedEvent("cell", 7, due, 3) { EventCore = EventCore.Create("tenant") };

        provider.GetRequiredService<ScheduleRuntimeCellDeletionAt>()(request, due);

        var (job, state) = Assert.Single(jobs.Recorded);
        Assert.Equal(typeof(RuntimeCellDeletionJob), job.Type);
        Assert.Equal(nameof(RuntimeCellDeletionJob.ExecuteAsync), job.Method.Name);
        Assert.Equal(request, job.Args[0]);
        Assert.Equal(due, Assert.IsType<ScheduledState>(state).EnqueueAt);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("1", 1)]
    [InlineData("60", 60)]
    public void HangfirePolling_BindsValidatedDefaultsAndBounds(string? configured, int expected)
    {
        using var provider = SchedulingProvider(configured);
        Assert.Equal(expected, provider.GetRequiredService<IOptions<HangfireSchedulingOptions>>().Value.SchedulePollingIntervalSeconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    public void HangfirePolling_RejectsInvalidBounds(string configured)
    {
        using var provider = SchedulingProvider(configured);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<HangfireSchedulingOptions>>().Value);
    }

    private static ServiceProvider SchedulingProvider(string? configured)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null)
            values["Hangfire:SchedulePollingIntervalSeconds"] = configured;
        return new ServiceCollection().AddHangfireWithMongo(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
    }

    private static ConsumeContext<RuntimeCellDeletionRequestedEvent> Context(RuntimeCellDeletionRequestedEvent message)
    {
        var context = DispatchProxy.Create<ConsumeContext<RuntimeCellDeletionRequestedEvent>, RequestContext>();
        ((RequestContext)(object)context).Message = message;
        return context;
    }

    public class RequestContext : DispatchProxy
    {
        public RuntimeCellDeletionRequestedEvent Message { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_Message" ? Message : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class RecordingJobs : IBackgroundJobClient
    {
        public List<(Job Job, IState State)> Recorded { get; } = [];
        public string Create(Job job, IState state) { Recorded.Add((job, state)); return Recorded.Count.ToString(); }
        public bool ChangeState(string jobId, IState state, string expectedState) => throw new NotSupportedException();
    }
}
