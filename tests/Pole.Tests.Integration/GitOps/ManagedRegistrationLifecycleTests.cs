using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle.Mongo;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Persistence;
using Pole.Features.Postgres.Application.Persistence.Lifecycle;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Theory]
    [InlineData("own")]
    [InlineData("rollback")]
    [InlineData("external")]
    public async Task Managed_credential_registration_follows_its_owner_and_proves_both_cleanup_fences(string scenario)
    {
        await SeedPostgresPlanningCatalog();
        var postgres = await InitializePostgres();
        var credential = await Secrets.Find(item => item.Management is ProviderManagedSecret).SingleAsync();
        var registrations = await DbContext.ResourceRegistrations.Find(item => item.ResourceGroupId == postgres.GroupId)
            .ToListAsync();
        var credentialRegistration = registrations.Single(item => item.ResourceId == credential.Id);
        var ownerRegistration = registrations.Single(item => item.ResourceId == postgres.Id);
        var (owner, preview) = await PreviewManagedGroupRemovalAsync();
        Assert.DoesNotContain(preview.Operations, item => item.Resource.ResourceId == credential.Id);
        var mapped = preview.DependencySets.Single(item => item.Owner == "ResourceGroups" &&
            item.Target.ResourceId == postgres.GroupId).Dependencies.Where(item =>
                item.Dependent.ResourceId == postgres.Id).ToArray();
        Assert.Equal(2, mapped.Length);
        Assert.Contains(mapped, item => item.Versions.Any(version =>
            version.DocumentId == $"ResourceRegistration/{credentialRegistration.Id}"));
        Assert.Contains(mapped, item => item.Versions.Any(version =>
            version.DocumentId == $"ResourceRegistration/{ownerRegistration.Id}"));
        Assert.Contains(mapped.SelectMany(item => item.Versions), item =>
            item.DocumentId == $"Secret/{credential.Id}/ReferenceFence");
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await Services.GetRequiredService<GitLifecyclePreviews>()
            .ConfirmAsync(new(new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default))
            .Value);
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        var deployment = await DbContext.ResourceDeployments.Find(item => item.ResourceId == postgres.Id).SingleAsync();
        var now = deployment.UpdatedAtUtc.AddSeconds(1);
        var claim = Id();
        var deleting = deployment.Start(deployment.DesiredGeneration, ResourceDeploymentOperation.Delete,
            claim, now, now.AddMinutes(2));
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id,
            deleting.Complete(deployment.DesiredGeneration, ResourceDeploymentOperation.Delete, claim, [],
                now.AddSeconds(1)));
        var before = await DbContext.ResourceGroups.Find(item => item.Id == postgres.GroupId).SingleAsync();
        if (scenario == "rollback")
        {
            using var failing = TestServiceProviderFactory.Create(Fixture, services =>
                services.AddScoped<StoreOutboxWrites>(_ => (_, _, _) => throw new ManagedRegistrationRollback()));
            using var scope = failing.CreateScope();
            await Assert.ThrowsAsync<ManagedRegistrationRollback>(() => scope.ServiceProvider
                .GetRequiredService<FinalizePostgresClusterDeletion>().Invoke(postgres.TenantId, postgres.Id, default));
            Assert.Equal(before.RegistrationFence, (await DbContext.ResourceGroups.Find(item =>
                item.Id == postgres.GroupId).SingleAsync()).RegistrationFence);
            Assert.True(await Secrets.Find(item => item.Id == credential.Id).AnyAsync());
            Assert.Equal(2, await DbContext.ResourceRegistrations.CountDocumentsAsync(item =>
                item.Id == credentialRegistration.Id || item.Id == ownerRegistration.Id));
            Assert.Equal(0, await Services.GetRequiredService<IMongoCollection<GitLifecycleCleanupProof>>()
                .CountDocumentsAsync(item => item.RunId == scheduled.RunId && item.Subject.ResourceId == postgres.Id));
        }
        if (scenario == "external")
            await DbContext.ResourceGroups.UpdateOneAsync(item => item.Id == postgres.GroupId,
                Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1));

        Assert.IsType<PostgresOutcome<Unit, PostgresClusterDeletionFinalizationError>.PostgresSuccess>((await Services
            .GetRequiredService<FinalizePostgresClusterDeletion>().Invoke(postgres.TenantId, postgres.Id, default)));
        var proofs = await Services.GetRequiredService<IMongoCollection<GitLifecycleCleanupProof>>()
            .Find(item => item.RunId == scheduled.RunId && item.Subject.ResourceId == postgres.Id).ToListAsync();
        Assert.Equal(2, proofs.Count);
        var begin = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
            item.RunId == scheduled.RunId && item.ResourceId == postgres.Id &&
            item.Lifecycle!.Step == GitLifecycleStep.BeginRemoval).SingleAsync();
        Assert.All(proofs, item => Assert.Equal(begin.Id, item.BeginReceiptId));
        Assert.Contains(proofs, item => item.CleanupId ==
            $"ResourceRegistration/{credentialRegistration.Id}/Unregistered");
        Assert.Contains(proofs, item => item.CleanupId == $"ResourceRegistration/{ownerRegistration.Id}/Unregistered");
        var transitions = proofs.SelectMany(item => item.Transitions).OrderBy(item => item.Before).ToArray();
        Assert.Equal(2, transitions.Length);
        Assert.Equal(transitions[0].After, transitions[1].Before);
        Assert.All(transitions, item => Assert.Equal($"ResourceGroupFence/{postgres.GroupId}", item.DocumentId));
        Assert.False(await Secrets.Find(item => item.Id == credential.Id).AnyAsync());
        var pending = (await Services.GetRequiredService<GitRepositorySources>()
            .RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
        await ResumeConnectionLifecycleAsync(pending);
        if (scenario == "external")
        {
            var blocked = (await Services.GetRequiredService<GitRepositorySources>()
                .RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
            Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(blocked.State).Completion);
            Assert.Contains(blocked.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
            Assert.True(await DbContext.ResourceGroups.Find(item => item.Id == postgres.GroupId).AnyAsync());
            return;
        }

        await CompleteManagedGroupCleanupAsync(owner, scheduled.RunId);
        var complete = (await Services.GetRequiredService<GitRepositorySources>()
            .RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(complete.State).Completion);
        Assert.Equal(0, await Inventory.CountDocumentsAsync(item => item.SourceId == owner.Id));
        Assert.False(await DbContext.ResourceGroups.Find(item => item.Id == postgres.GroupId).AnyAsync());
        Assert.Equal(0, await DbContext.ResourceRegistrations.CountDocumentsAsync(item =>
            item.ResourceGroupId == postgres.GroupId));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("foreign-tenant")]
    [InlineData("other-group")]
    [InlineData("unknown")]
    [InlineData("extra-binding")]
    [InlineData("user-secret")]
    public async Task Managed_registration_does_not_hide_unreviewed_or_unavailable_owner_children(string scenario)
    {
        await SeedPostgresPlanningCatalog();
        var postgres = await InitializePostgres();
        var credential = await Secrets.Find(item => item.Management is ProviderManagedSecret).SingleAsync();
        var registration = await DbContext.ResourceRegistrations.Find(item => item.ResourceId == postgres.Id)
            .SingleAsync();
        if (scenario == "manual")
            await Inventory.DeleteOneAsync(item => item.ResourceId == postgres.Id);
        else if (scenario == "foreign")
            await Inventory.UpdateOneAsync(item => item.ResourceId == postgres.Id,
                Builders<ManagedDeclaration>.Update.Set(item => item.SourceId, Id()));
        else if (scenario == "missing")
            await DbContext.ResourceRegistrations.DeleteOneAsync(item => item.Id == registration.Id);
        else if (scenario is "foreign-tenant" or "other-group")
            await DbContext.ResourceRegistrations.ReplaceOneAsync(item => item.Id == registration.Id,
                scenario == "foreign-tenant" ? registration with { TenantId = Id() } :
                    registration with { ResourceGroupId = Id() });
        else if (scenario == "extra-binding")
            await Secrets.ReplaceOneAsync(item => item.Id == credential.Id, credential with
            {
                Bindings = credential.Bindings.Add(SecretBinding.Create(Id(), "pole/postgresql", Id(),
                    new AllSecretDeployments(), [])),
                Revision = credential.Revision + 1
            });
        else if (scenario == "user-secret")
            await InsertUnreviewedUserSecretAsync(postgres.TenantId, postgres.GroupId, credential.IsolationScopeId);
        else
        {
            var owner = Assert.IsType<ProviderManagedSecret>(credential.Management);
            await Secrets.ReplaceOneAsync(item => item.Id == credential.Id, credential with
            {
                Management = owner with { ResourceType = "plugin/unknown" },
                Bindings = [credential.Bindings[0] with { Consumer = new("plugin/unknown", postgres.Id) }],
                Revision = credential.Revision + 1
            });
            await DbContext.ResourceRegistrations.InsertOneAsync(ResourceRegistrationFactory.Create(postgres.TenantId,
                postgres.GroupId, ResourceTypeId.Create("plugin/unknown"), postgres.Id, "unknown", "plugin",
                new(Id(), DateTime.UtcNow)));
        }

        var ownerSource = (await Services.GetRequiredService<GitRepositorySources>()
            .LoadAsync(TestData.TenantId, source, default))!;
        repository.Publish(ownerSource.Repository.ExternalRepositoryId, [], "remove-managed-group");
        var result = await Services.GetRequiredService<GitLifecyclePreviews>().CreateAsync(
            new(new(ownerSource.TenantId, ownerSource.Id), ownerSource.Revision,
                new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default);
        var failure = Assert.IsType<GitPreviewRejected>(result.Value).Failure;
        Assert.True(failure is GitLifecycleDependencyRetained or GitLifecycleUnsupported, failure.GetType().Name);
        Assert.True(await PostgresResources.Find(item => item.Id == postgres.Id).AnyAsync());
        Assert.True(await Secrets.Find(item => item.Id == credential.Id).AnyAsync());
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("registration")]
    [InlineData("phantom")]
    public async Task Managed_registration_preview_rejects_changed_metadata_and_complete_registration_sets(
        string change)
    {
        await SeedPostgresPlanningCatalog();
        var postgres = await InitializePostgres();
        var credential = await Secrets.Find(item => item.Management is ProviderManagedSecret).SingleAsync();
        var (owner, preview) = await PreviewManagedGroupRemovalAsync();
        if (change == "metadata")
            await Secrets.UpdateOneAsync(item => item.Id == credential.Id,
                Builders<Secret>.Update.Inc(item => item.ReferenceFence, 1));
        else if (change == "registration")
            await DbContext.ResourceRegistrations.UpdateOneAsync(item => item.ResourceId == credential.Id,
                Builders<ResourceRegistration>.Update.Inc(item => item.ReferenceFence, 1));
        else
            await InsertUnreviewedUserSecretAsync(postgres.TenantId, postgres.GroupId, credential.IsolationScopeId);

        Assert.IsType<GitPreviewStale>((await Services.GetRequiredService<GitLifecyclePreviews>().ConfirmAsync(
            new(new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
    }

    [Fact]
    public async Task Registration_ownership_is_tenant_scoped_and_uses_real_metadata_fences()
    {
        await SeedPostgresPlanningCatalog();
        await InitializePostgres();
        var credential = await Secrets.Find(item => item.Management is ProviderManagedSecret).SingleAsync();
        var provider = Assert.IsAssignableFrom<IDeclarativeRegistrationOwnershipProvider>(Services
            .GetServices<IDeclarativeResourceProvider>().Single(item => item.Kind == "Secret"));
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<DeclarationReadBlocked>((await provider.ReadRegistrationOwnershipAsync(
            new(Id(), credential.Id), session, default)).Value);
        var read = Assert.IsType<ManagedRegisteredResourceOwnership>((await provider.ReadRegistrationOwnershipAsync(
            new(credential.TenantId, credential.Id), session, default)).Value);
        Assert.IsType<DeclarationFenced>((await provider.FenceRegistrationOwnershipAsync(
            new(credential.TenantId, credential.Id, read.Version), session, default)).Value);
        Assert.IsType<DeclarationPreconditionsChanged>((await provider.FenceRegistrationOwnershipAsync(
            new(credential.TenantId, credential.Id, read.Version), session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(credential.ReferenceFence, (await Secrets.Find(item => item.Id == credential.Id).SingleAsync())
            .ReferenceFence);
    }

    private sealed class ManagedRegistrationRollback : Exception;

    private async Task<(RepositorySource Source, GitOperationPreview Preview)> PreviewManagedGroupRemovalAsync()
    {
        var owner = (await Services.GetRequiredService<GitRepositorySources>()
            .LoadAsync(TestData.TenantId, source, default))!;
        repository.Publish(owner.Repository.ExternalRepositoryId, [], "remove-managed-group");
        var result = await Services.GetRequiredService<GitLifecyclePreviews>().CreateAsync(new(
            new(owner.TenantId, owner.Id), owner.Revision,
            new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default);
        Assert.True(result is GitOperationPreview, result.Value is GitPreviewRejected rejected
            ? rejected.Failure.GetType().Name : result.Value.ToString());
        return (owner, Assert.IsType<GitOperationPreview>(result.Value));
    }

    private async Task InsertUnreviewedUserSecretAsync(string tenant, string group, string scope)
    {
        Assert.IsType<Secret>((await Services.GetRequiredService<PersistCreatedSecret>()(tenant, group, scope,
            "manual-child", "manual", new() { ["key"] = "fixture-value" }, default)).Value);
    }

    private async Task CompleteManagedGroupCleanupAsync(RepositorySource owner, string runId)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var scopes = await DbContext.IsolationScopes.Find(item => item.TenantId == owner.TenantId).ToListAsync();
            foreach (var scope in scopes.Where(item => item.Lifecycle is DeletingIsolationScopeLifecycleState))
            {
                foreach (var cell in await DbContext.RuntimeCells.Find(item => item.IsolationScopeId == scope.Id)
                             .ToListAsync())
                    if (cell.State is DeletingRuntimeCellState)
                        await DbContext.RuntimeCells.ReplaceOneAsync(item => item.Id == cell.Id,
                            cell.MarkDeleted(cell.DesiredGeneration, cell.UpdatedAtUtc.AddSeconds(1)));
                await Services.GetRequiredService<FinalizeResourceGroupDeletion>()(owner.TenantId,
                    scope.ResourceGroupId, scope.Id);
            }
            var run = (await Services.GetRequiredService<GitRepositorySources>()
                .RunAsync(owner.TenantId, owner.Id, runId, default))!;
            if (run.State is GitRunFinished)
                return;
            await ResumeConnectionLifecycleAsync(run);
        }
    }
}
