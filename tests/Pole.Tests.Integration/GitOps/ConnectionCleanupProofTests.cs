using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
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
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class ConnectionCleanupProofTests(MongoReplicaSetFixture fixture) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_cleanup_status_proves_its_own_revision_but_never_an_external_gap(bool external)
    {
        var final = await ApplyDeferredConnectionCleanupAsync(external);
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == final.Id).ToListAsync();
        Assert.Equal(!external, receipts.Any(item => item.Lifecycle?.Step == GitLifecycleStep.RemovalCompleted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_workload_apply_advances_own_group_fences_but_rejects_external_changes(bool external)
    {
        var intercepted = false;
        var mutations = new BeforeNoOp(Services.GetRequiredService<IGitOpsMutations>(), async (operation, ct) =>
        {
            if (intercepted || operation.Declaration.Address.Kind != "WebService" ||
                operation.Phase != DeclarationPhase.Configure)
                return;

            intercepted = true;
            if (external)
                await DbContext.ResourceGroups.UpdateOneAsync(item => item.TenantId == Data.TenantId,
                    Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1), cancellationToken: ct);
        });
        var snapshots = ActivatorUtilities.CreateInstance<GitAcceptedSnapshotExecutor>(Services, mutations);
        var lifecycle = ActivatorUtilities.CreateInstance<GitLifecycleExecutor>(Services, snapshots);
        var worker = ActivatorUtilities.CreateInstance<RepositorySourceJob>(Services, snapshots, lifecycle);
        var final = await ApplyDeferredConnectionCleanupAsync(false, worker);
        Assert.True(intercepted);
        Assert.Equal(external ? GitRunCompletion.Blocked : GitRunCompletion.ConfigurationApplied,
            Assert.IsType<GitRunFinished>(final.State).Completion);
        if (external)
            Assert.Contains(final.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
        else
            Assert.Empty(final.Diagnostics);
    }
}
