using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected async Task ResumeConnectionLifecycleAsync(GitReconciliationRun run)
    {
        var waiting = Assert.IsType<GitRunWaitingCleanup>(run.State);
        await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>()
            .ReplaceOneAsync(
                item => item.Id == run.Id,
                run with
                {
                    State = new GitRunWaitingCleanup(waiting.LastAttempt, DateTime.UtcNow.AddMinutes(-1)),
                });
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(run.Stamp.Source, default);
    }
}
