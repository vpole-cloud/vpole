using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

internal enum RepositoryPipelineStatus
{
    InvalidSnapshot, StaleExecution, ConfigurationApplied, Blocked, Applying, Failed
}

internal sealed record RepositoryPipelineResult(
    CompiledSnapshot Snapshot,
    GitMutationContext? Context,
    IReadOnlyList<GitOperationResult> Operations,
    RepositoryPipelineStatus Status);

// Exercises the real repository worker. Provider tests seed server-owned source identities, while API tests
// exercise admission and source creation separately. This is not a production YAML/manual compatibility path.
internal sealed class RepositoryPipelineDriver(
    IServiceProvider services,
    MongoReplicaSetFixture fixture,
    TestRepositorySnapshots repository,
    DeclarativeProviderRegistry? customProviders = null,
    IGitOpsMutations? customMutations = null)
{
    private GitRepositorySources Sources => services.GetRequiredService<GitRepositorySources>();

    private IMongoCollection<GitReconciliationRun> Runs => fixture.Database
        .GetCollection<GitReconciliationRun>("GitReconciliationRuns");

    public async Task<RepositoryPipelineResult> SubmitAsync(
        GitSourceIdentity identity,
        IReadOnlyList<ManifestFile> files,
        string label = "snapshot")
    {
        var source = await Sources.LoadAsync(identity.TenantId, identity.SourceId, default);
        if (source is null)
        {
            source = RepositorySourceFactory.Create(identity,
                new(repository.Provider, TestRepositorySnapshots.ConnectionId, identity.SourceId, "main", ""),
                new(TimeSpan.FromMinutes(5)),
                new(TestData.UserId, DateTime.UtcNow),
                Guid.NewGuid().ToString("D"),
                "fixture",
                TestRepositorySnapshots.DisplayUrl);
            await fixture.Database.GetCollection<RepositorySource>("GitRepositorySources").InsertOneAsync(source);
            await fixture.Database.GetCollection<GitSynchronizationState>("GitSynchronizationStates").InsertOneAsync(
                GitSynchronizationStateFactory.Create(identity, DateTime.UtcNow.AddMinutes(5)));
        }

        repository.Publish(source.Repository.ExternalRepositoryId, files, label);
        Assert.IsType<GitSourceScheduled>((await Sources.RequestSyncAsync(new(identity.TenantId,
                identity.SourceId,
                source.Revision,
                Guid.NewGuid().ToString("D"),
                new(GitRunTrigger.Manual, TestData.UserId)),
            default)).Value);
        return await RecoverAsync(identity);
    }

    public async Task<RepositoryPipelineResult> ResumeAsync(GitMutationContext context)
    {
        var state = await Sources.StateAsync(context.TenantId, context.SourceId, default);
        if (state.LatestRunId != context.RunId)
            return Stale();

        var run = (await Sources.RunAsync(context.TenantId, context.SourceId, context.RunId, default))!;
        if (run.State is GitRunFinished { Completion: GitRunCompletion.Blocked or GitRunCompletion.Failed })
        {
            var retry = await Sources.RetryAsync(new(new(context.TenantId,
                        context.SourceId,
                        context.Authority.ConfigurationRevision,
                        Guid.NewGuid().ToString("D"),
                        new(GitRunTrigger.Manual, TestData.UserId)),
                    context.RunId),
                default);
            if (retry is not GitSourceScheduled)
                return Stale();
        }

        return await RecoverAsync(new(context.TenantId, context.SourceId));
    }

    public async Task<RepositoryPipelineResult> RecoverAsync(GitSourceIdentity identity)
    {
        RepositorySourceJob worker;
        if (customProviders is null && customMutations is null)
            worker = services.GetRequiredService<RepositorySourceJob>();
        else
        {
            var executorOverrides = new List<object>();
            var workerOverrides = new List<object>();
            if (customProviders is not null)
            {
                var overrideCompiler = new ManifestCompiler(customProviders);
                executorOverrides.Add(customProviders);
                executorOverrides.Add(overrideCompiler);
                workerOverrides.Add(overrideCompiler);
            }
            if (customMutations is not null)
                executorOverrides.Add(customMutations);
            workerOverrides.Add(ActivatorUtilities.CreateInstance<GitAcceptedSnapshotExecutor>(services,
                executorOverrides.ToArray()));
            worker = ActivatorUtilities.CreateInstance<RepositorySourceJob>(services, workerOverrides.ToArray());
        }
        var report = await worker.ProcessAsync(identity, default);
        var state = await Sources.StateAsync(identity.TenantId, identity.SourceId, default);
        var run = (await Sources.RunAsync(identity.TenantId, identity.SourceId, state.LatestRunId!, default))!;
        var compiler = customProviders is null
            ? services.GetRequiredService<ManifestCompiler>()
            : new ManifestCompiler(customProviders);
        var snapshot = run.Input is GitInputAccepted accepted
            ? compiler.Restore(accepted.Snapshot.ApiVersion,
                accepted.Snapshot.Resources.Select(item => new AcceptedManifestDeclaration(
                    item.Kind,
                    item.Name,
                    item.SpecJson)).ToArray())
            : new CompiledSnapshot([],
                run.Diagnostics.Select(item =>
                        new DeclarationDiagnostic(item.File ?? "", item.Document ?? 0, item.Field ?? "", item.Code))
                    .ToArray());
        var status = report.Disposition == RepositoryExecutionDisposition.LeaseLost
            ? RepositoryPipelineStatus.StaleExecution
            : run.State switch
            {
                GitRunFinished { Completion: GitRunCompletion.ConfigurationApplied or GitRunCompletion.AlreadyCurrent }
                    => RepositoryPipelineStatus.ConfigurationApplied,
                GitRunFinished { Completion: GitRunCompletion.Blocked } => RepositoryPipelineStatus.Blocked,
                GitRunFinished { Completion: GitRunCompletion.Superseded } => RepositoryPipelineStatus.StaleExecution,
                GitRunFinished when run.Input is not GitInputAccepted &&
                                    run.Diagnostics.Any(item => item.Document != null)
                    => RepositoryPipelineStatus.InvalidSnapshot,
                GitRunFinished => RepositoryPipelineStatus.Failed,
                GitRunWaiting or GitRunClaimed or GitRunDelayed or GitRunWaitingCleanup =>
                    RepositoryPipelineStatus.Applying
            };
        return new(snapshot, report.Context, report.Operations, status);
    }

    public async Task<GitReconciliationRun> LatestAsync(string source)
    {
        var state = await Sources.StateAsync(TestData.TenantId, source, default);
        return (await Sources.RunAsync(TestData.TenantId, source, state.LatestRunId!, default))!;
    }

    public async Task MakeRecoveryDueAsync(string source)
    {
        var run = await LatestAsync(source);
        var due = DateTime.UtcNow.AddMinutes(-1);
        var state = run.State switch
        {
            GitRunClaimed claimed => new GitRunClaimed(claimed.Lease with { ExpiresAtUtc = due }),
            GitRunDelayed delayed => (GitRunState)(delayed with { NextAttemptAtUtc = due }),
            GitRunWaiting or GitRunWaitingCleanup or GitRunFinished =>
                throw new InvalidOperationException("Only an interrupted active run can be recovered.")
        };
        await Runs.UpdateOneAsync(item => item.Id == run.Id,
            Builders<GitReconciliationRun>.Update.Set(item => item.State, state));
    }

    private static RepositoryPipelineResult Stale() =>
        new(new([], []), null, [], RepositoryPipelineStatus.StaleExecution);
}
