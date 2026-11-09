using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Application.Common;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected async Task ResumeConnectionLifecycleAsync(GitReconciliationRun run)
    {
        var waiting = Assert.IsType<GitRunWaitingCleanup>(run.State);
        await Services.GetRequiredService<IMongoCollection<GitReconciliationRun>>().ReplaceOneAsync(
            item => item.Id == run.Id, run with
            { State = new GitRunWaitingCleanup(waiting.LastAttempt, DateTime.UtcNow.AddMinutes(-1)) });
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(run.Stamp.Source, default);
    }
}
