using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Events;
using Pole.Features.Outbox.Contracts;
using Pole.Observability;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class GitRunResumptionTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private const string WorkRequestedEvent = "pole.gitops/v1/source-work-requested";
    private bool _rejectResumptionOutbox;
    private GitRunStore Store => Services.GetRequiredService<GitRunStore>();
    private GitRepositorySources Sources => Services.GetRequiredService<GitRepositorySources>();
    private IMongoCollection<GitReconciliationRun> Runs =>
        Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<StoreOutboxWrites>(_ => async (session, writes, token) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, token);
            if (_rejectResumptionOutbox &&
                writes.Any(write => write.Event is GitSourceWorkRequested { NotBeforeUtc: not null }))
                throw new ResumptionOutboxFailure();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_and_delayed_runs_atomically_request_their_persisted_due_time(bool delayed)
    {
        var claim = await ClaimNewSource();
        var trace = TraceContext.Create("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "gitops-resumption");
        using (TraceContext.Push(trace))
            Assert.True(await Suspend(claim.Handle, delayed));

        var run = await LoadRun(claim);
        var due = Due(run.State, delayed);
        var message = Assert.Single(await ResumptionMessages(claim));
        Assert.Equal(due, message.NotBeforeUtc);
        Assert.Equal(claim.Handle.Stamp.Source.SourceId, message.SourceId);
        Assert.Equal(claim.Handle.Stamp.Source.TenantId, message.EventCore.TenantId);
        Assert.Equal(trace.TraceId, message.EventCore.TraceId);
        Assert.Equal(trace.CorrelationId, message.EventCore.CorrelationId);
        Assert.IsType<GitNoWorkAvailable>((await Store.ClaimAsync(claim.Handle.Stamp.Source, "early", default)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_outbox_write_rolls_back_the_waiting_transition_and_its_message(bool delayed)
    {
        var claim = await ClaimNewSource();
        var previousState = await Sources.StateAsync(claim.Source.TenantId, claim.Source.Id, default);
        _rejectResumptionOutbox = true;

        await Assert.ThrowsAsync<ResumptionOutboxFailure>(() => Suspend(claim.Handle, delayed));

        var run = await LoadRun(claim);
        Assert.Equal(claim.Handle.Identity.WorkerId, Assert.IsType<GitRunClaimed>(run.State).Lease.WorkerId);
        Assert.Equal(0, run.ConsecutiveFailures);
        var state = await Sources.StateAsync(claim.Source.TenantId, claim.Source.Id, default);
        Assert.Equal(previousState.MutationVersion, state.MutationVersion);
        Assert.Equal(previousState.CurrentRun, state.CurrentRun);
        Assert.Empty(await ResumptionMessages(claim));
    }

    [Fact]
    public async Task A_stale_lease_cannot_save_a_waiting_transition_or_publish_a_wake_up()
    {
        var first = await ClaimNewSource();
        await Runs.UpdateOneAsync(item => item.Id == first.Run.Id,
            Builders<GitReconciliationRun>.Update.Set(item => item.State,
                new GitRunClaimed(new(first.Handle.Identity.WorkerId,
                    first.Handle.Identity.Attempt, DateTime.UtcNow.AddMinutes(-1)))));
        var next = Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(first.Handle.Stamp.Source,
            "replacement", default)).Value);

        Assert.False(await Store.WaitCleanupAsync(first.Handle, default));
        Assert.False(await Store.DelayAsync(first.Handle, new("TemporaryUnavailable", null, null, null), default));

        Assert.Equal(next.Handle.Identity.WorkerId, Assert.IsType<GitRunClaimed>((await LoadRun(next)).State).Lease.WorkerId);
        Assert.Empty(await ResumptionMessages(first));
    }

    [Fact]
    public async Task Cancellation_cannot_publish_a_wake_up_without_a_waiting_transition()
    {
        var claim = await ClaimNewSource();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Store.WaitCleanupAsync(claim.Handle, cancellation.Token));

        Assert.IsType<GitRunClaimed>((await LoadRun(claim)).State);
        Assert.Empty(await ResumptionMessages(claim));
    }

    [Fact]
    public async Task A_terminal_completion_does_not_schedule_a_new_attempt()
    {
        var claim = await ClaimNewSource();

        Assert.True(await Store.CompleteAsync(claim.Handle, GitRunCompletion.Failed, [], default));

        Assert.IsType<GitRunFinished>((await LoadRun(claim)).State);
        Assert.Empty(await ResumptionMessages(claim));
    }

    [Fact]
    public async Task Duplicate_due_jobs_have_only_one_effective_claim()
    {
        var first = await ClaimNewSource();
        Assert.True(await Store.WaitCleanupAsync(first.Handle, default));
        await MakeDue(first);

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            Store.ClaimAsync(first.Handle.Stamp.Source, $"redelivery-{index}", default)));

        var next = Assert.Single(claims.Select(outcome => outcome.Value).OfType<GitWorkClaim>());
        Assert.Equal(first.Run.Id, next.Run.Id);
        Assert.Equal(first.Handle.Identity.Attempt + 1, next.Handle.Identity.Attempt);
        Assert.Equal(3, claims.Count(outcome => outcome.Value is GitNoWorkAvailable));
    }

    [Fact]
    public async Task Recovery_scanner_rediscovers_due_cleanup_when_the_scheduled_enqueue_is_lost()
    {
        var first = await ClaimNewSource();
        Assert.True(await Store.WaitCleanupAsync(first.Handle, default));
        await MakeDue(first);
        var dispatched = new List<GitSourceIdentity>();
        var scanner = new GitSourceRecoveryScanner(Services.GetRequiredService<GitSourceScheduling>(),
            dispatched.Add, NullLogger<GitSourceRecoveryScanner>.Instance);

        await scanner.ScanAsync(default);

        Assert.Contains(first.Handle.Stamp.Source, dispatched);
        Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(first.Handle.Stamp.Source, "recovered", default)).Value);
    }

    private Task<bool> Suspend(GitRunHandle handle, bool delayed) => delayed
        ? Store.DelayAsync(handle, new("TemporaryUnavailable", null, null, null), default)
        : Store.WaitCleanupAsync(handle, default);

    private static DateTime Due(GitRunState state, bool delayed) => delayed
        ? Assert.IsType<GitRunDelayed>(state).NextAttemptAtUtc
        : Assert.IsType<GitRunWaitingCleanup>(state).NextAttemptAtUtc;

    private Task<GitReconciliationRun> LoadRun(GitWorkClaim claim) =>
        Runs.Find(item => item.Id == claim.Run.Id && item.Stamp.Source == claim.Handle.Stamp.Source).SingleAsync();

    private async Task MakeDue(GitWorkClaim claim)
    {
        var waiting = Assert.IsType<GitRunWaitingCleanup>((await LoadRun(claim)).State);
        await Runs.UpdateOneAsync(item => item.Id == claim.Run.Id,
            Builders<GitReconciliationRun>.Update.Set(item => item.State,
                waiting with { NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-1) }));
    }

    private async Task<GitSourceWorkRequested[]> ResumptionMessages(GitWorkClaim claim)
    {
        var messages = await Fixture.DbContext.OutboxMessages.Find(message =>
            message.TenantId == claim.Source.TenantId && message.EventType == WorkRequestedEvent).ToListAsync();
        return messages.Select(message => JsonSerializer.Deserialize<GitSourceWorkRequested>(message.PayloadJson,
                EventExtensions.SerializerOptions)!)
            .Where(message => message.SourceId == claim.Source.Id && message.NotBeforeUtc is not null)
            .ToArray();
    }

    private async Task<GitWorkClaim> ClaimNewSource()
    {
        var source = Assert.IsType<RepositorySource>((await Sources.CreateAsync(new(TestData.TenantId,
            Guid.NewGuid().ToString("D"), new("Example", "connection", "repository", "main", ""),
            new(TimeSpan.FromMinutes(5)), new(TestData.UserId, DateTime.UtcNow), "https://example.test/repository"),
            default)).Value);
        return Assert.IsType<GitWorkClaim>((await Store.ClaimAsync(new(source.TenantId, source.Id),
            "original", default)).Value);
    }

    private sealed class ResumptionOutboxFailure : Exception;
}
