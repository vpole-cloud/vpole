using Hangfire;
using MassTransit;
using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public interface ISourceBuildJobExecutor
{
    Task ExecuteAsync(string buildJobId);
}

public interface ISourceBuildJobDispatcher
{
    string Enqueue(string buildJobId);
}

public sealed class HangfireSourceBuildJobDispatcher(IBackgroundJobClient backgroundJobs)
    : ISourceBuildJobDispatcher
{
    public string Enqueue(string buildJobId) =>
        backgroundJobs.Enqueue<ISourceBuildJobExecutor>(executor => executor.ExecuteAsync(buildJobId));
}

public sealed class EnqueueSourceBuildOnRequestedConsumer(ISourceBuildJobDispatcher dispatcher)
    : IConsumer<SourceBuildRequestedEvent>
{
    public Task Consume(ConsumeContext<SourceBuildRequestedEvent> context)
    {
        dispatcher.Enqueue(context.Message.BuildJobId);
        return Task.CompletedTask;
    }
}

public sealed class EnqueueSourceBuildOnRequestedConsumerDefinition
    : ConsumerDefinition<EnqueueSourceBuildOnRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<EnqueueSourceBuildOnRequestedConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseMessageRetry(retry =>
            retry.Intervals(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)));
}

public sealed class SourceBuildQueueRecoveryJob(
    ISourceBuildDbContext dbContext,
    ISourceBuildJobDispatcher dispatcher,
    TimeProvider timeProvider)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync()
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-30);
        var queued = await dbContext.SourceBuildJobs
            .Find(Builders<SourceBuildJob>.Filter.And(
                Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, QueuedSourceBuild>(
                    item => item.State),
                Builders<SourceBuildJob>.Filter.Lte(item => item.UpdatedAtUtc, cutoff)))
            .SortBy(item => item.UpdatedAtUtc)
            .Limit(100)
            .Project(item => item.Id)
            .ToListAsync();

        foreach (var buildJobId in queued)
            dispatcher.Enqueue(buildJobId);
    }
}
