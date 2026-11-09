using System.Reflection;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Events;
using Xunit;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Features.GitOps.Tests;

public sealed class GitSourceDispatchTests
{
    [Fact]
    public async Task Existing_requests_dispatch_the_event_tenant_and_source_immediately()
    {
        var dispatched = new List<GitSourceIdentity>();
        var scheduled = new List<(GitSourceIdentity Source, DateTime Due)>();
        var consumer = new EnqueueGitSourceOnRequested(dispatched.Add,
            (source, due) => scheduled.Add((source, due)), NullLogger<EnqueueGitSourceOnRequested>.Instance);

        await consumer.Consume(Context(new("source", EventCore.Create("tenant"))));

        Assert.Equal(new("tenant", "source"), Assert.Single(dispatched));
        Assert.Empty(scheduled);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Due_requests_schedule_the_exact_persisted_time_including_late_delivery(int minuteOffset)
    {
        var dispatched = new List<GitSourceIdentity>();
        var scheduled = new List<(GitSourceIdentity Source, DateTime Due)>();
        var consumer = new EnqueueGitSourceOnRequested(dispatched.Add,
            (source, due) => scheduled.Add((source, due)), NullLogger<EnqueueGitSourceOnRequested>.Instance);
        var notBeforeUtc = DateTime.UtcNow.AddMinutes(minuteOffset);

        await consumer.Consume(Context(new("source", EventCore.Create("tenant"), notBeforeUtc)));

        var actual = Assert.Single(scheduled);
        Assert.Equal(new("tenant", "source"), actual.Source);
        Assert.Equal(notBeforeUtc, actual.Due);
        Assert.Empty(dispatched);
    }

    [Fact]
    public async Task Scheduling_failure_reaches_message_retry_without_immediate_dispatch()
    {
        var dispatched = new List<GitSourceIdentity>();
        var expected = new InvalidOperationException("The job store is unavailable.");
        var consumer = new EnqueueGitSourceOnRequested(dispatched.Add, (_, _) => throw expected,
            NullLogger<EnqueueGitSourceOnRequested>.Instance);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(
            Context(new("source", EventCore.Create("tenant"), DateTime.UtcNow.AddMinutes(1)))));

        Assert.Same(expected, actual);
        Assert.Empty(dispatched);
    }

    [Fact]
    public void Existing_event_payload_without_due_time_remains_an_immediate_request()
    {
        const string payload = """{"sourceId":"source","eventCore":{"tenantId":"tenant"}}""";

        var message = JsonSerializer.Deserialize<GitSourceWorkRequested>(payload, EventExtensions.SerializerOptions);

        Assert.NotNull(message);
        Assert.Equal("source", message.SourceId);
        Assert.Equal("tenant", message.EventCore.TenantId);
        Assert.Null(message.NotBeforeUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registered_dispatchers_create_the_correct_Hangfire_job_and_state(bool delayed)
    {
        var jobs = new RecordingJobs();
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IBackgroundJobClient>(jobs)
            .AddGitOpsFeature()
            .BuildServiceProvider();
        using var scope = provider.CreateScope();
        var source = new GitSourceIdentity("tenant", "source");
        var notBeforeUtc = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        if (delayed)
            scope.ServiceProvider.GetRequiredService<ScheduleGitSourceAt>()(source, notBeforeUtc);
        else
            scope.ServiceProvider.GetRequiredService<DispatchGitSource>()(source);

        var (job, state) = Assert.Single(jobs.Recorded);
        Assert.Equal(typeof(RepositorySourceJob), job.Type);
        Assert.Equal(nameof(RepositorySourceJob.ExecuteAsync), job.Method.Name);
        Assert.Equal(source.TenantId, job.Args[0]);
        Assert.Equal(source.SourceId, job.Args[1]);
        if (delayed)
            Assert.Equal(notBeforeUtc, Assert.IsType<ScheduledState>(state).EnqueueAt);
        else
            Assert.IsType<EnqueuedState>(state);
    }

    private static ConsumeContext<GitSourceWorkRequested> Context(GitSourceWorkRequested message)
    {
        var context = DispatchProxy.Create<ConsumeContext<GitSourceWorkRequested>, WorkRequestedContext>();
        ((WorkRequestedContext)(object)context).Message = message;
        return context;
    }

    public class WorkRequestedContext : DispatchProxy
    {
        public GitSourceWorkRequested Message { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_Message"
                ? Message
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class RecordingJobs : IBackgroundJobClient
    {
        public List<(Job Job, IState State)> Recorded { get; } = [];

        public string Create(Job job, IState state)
        {
            Recorded.Add((job, state));
            return Recorded.Count.ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) =>
            throw new NotSupportedException();
    }
}
