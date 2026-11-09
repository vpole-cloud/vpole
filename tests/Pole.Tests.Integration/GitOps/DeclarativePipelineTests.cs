using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Migrations;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Migrations;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.SourceDelivery;
using Pole.Features.WebServices.Application.Tokens;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class DeclarativePipelineCoreTests(MongoReplicaSetFixture fixture) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("current", true)]
    [InlineData("stale-generation", false)]
    [InlineData("stale-revision", false)]
    [InlineData("stale-reference-fence", false)]
    [InlineData("stale-secret-revision", false)]
    [InlineData("wrong-tenant", false)]
    [InlineData("missing-placement", false)]
    [InlineData("duplicate-placement", false)]
    [InlineData("second-placement-unconfirmed", false)]
    [InlineData("both-placements-confirmed", true)]
    public async Task Retained_keys_are_released_only_with_complete_current_fenced_rollout_proof(string scenario,
        bool released)
    {
        var initial = await Apply(files: Snapshot(keys: ["NEXT_TOKEN", "API_TOKEN"]));
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        await Apply(Snapshot(keys: ["API_TOKEN", "NEXT_TOKEN"]));
        var secret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        var binding = Assert.Single(secret.Bindings);
        Assert.Equal(["API_TOKEN"], binding.Pins.Retained.ToArray());
        var deployment = await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Eq(item => item.TenantId, Data.TenantId))
            .SingleAsync();
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id,
            deployment with
            {
                State = new AppliedResourceDeploymentState(deployment.DesiredGeneration, DateTime.UtcNow),
                ObservedGeneration = deployment.DesiredGeneration
            });
        deployment = await DbContext.ResourceDeployments.Find(item => item.Id == deployment.Id).SingleAsync();
        IReadOnlyList<SecretConsumerDeploymentProof> proofs =
            [new(deployment.Id, deployment.Revision, deployment.DesiredGeneration)];
        if (scenario == "stale-generation")
            proofs = [proofs[0] with { DesiredGeneration = deployment.DesiredGeneration - 1 }];
        if (scenario == "stale-revision")
            proofs = [proofs[0] with { DeploymentRevision = deployment.Revision - 1 }];
        if (scenario == "missing-placement")
            proofs = [];
        if (scenario == "duplicate-placement")
            proofs = [proofs[0], proofs[0]];
        if (scenario is "second-placement-unconfirmed" or "both-placements-confirmed")
        {
            var second = GitOpsPlacementTestData.AdditionalPlacement(deployment);
            await DbContext.ResourceDeployments.InsertOneAsync(second);
            if (scenario == "both-placements-confirmed")
                proofs = [.. proofs, new(second.Id, second.Revision, second.DesiredGeneration)];
        }

        var originalRevision = secret.Revision;
        var originalFence = secret.ReferenceFence;
        if (scenario == "stale-reference-fence")
            await Secrets.UpdateOneAsync(item => item.Id == secret.Id,
                Builders<Secret>.Update.Inc(item => item.ReferenceFence, 1));
        if (scenario == "stale-secret-revision")
            await Secrets.UpdateOneAsync(item => item.Id == secret.Id,
                Builders<Secret>.Update.Inc(item => item.Revision, 1));
        var result = await Services.GetRequiredService<ReleaseRetainedSecretKeys>()(
            scenario == "wrong-tenant" ? Id() : secret.TenantId,
            secret.Id,
            originalRevision,
            originalFence,
            binding.Id,
            proofs,
            default);
        Assert.Equal(released, result);
        secret = await Secrets.Find(item => item.Id == secret.Id).SingleAsync();
        Assert.Equal(released, Assert.Single(secret.Bindings).Pins.Retained.IsEmpty);
        if (released)
        {
            Assert.Equal(originalRevision, secret.Revision);
            Assert.Equal(originalFence + 1, secret.ReferenceFence);
            if (scenario == "current")
            {
                var retired = await Apply(Snapshot(keys: ["NEXT_TOKEN"]));
                Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, retired.Status);
            }
        }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("applied-without-rollout-proof")]
    public async Task Retired_keys_remain_pinned_until_the_replacement_rollout_is_confirmed(string state)
    {
        var initial = await Apply(files: Snapshot(keys: ["NEXT_TOKEN", "API_TOKEN"]));
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var deployment = await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Eq(item => item.TenantId, Data.TenantId))
            .SingleAsync();
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id,
            deployment with
            {
                State = new AppliedResourceDeploymentState(deployment.DesiredGeneration, DateTime.UtcNow),
                ObservedGeneration = deployment.DesiredGeneration
            });
        var moved = await Apply(Snapshot(keys: ["API_TOKEN", "NEXT_TOKEN"]), commit: "move-reference");
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, moved.Status);
        deployment = await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        if (state == "failed")
            deployment = deployment with
            {
                State = new FailedResourceDeploymentState(deployment.DesiredGeneration,
                    ResourceDeploymentOperation.Apply,
                    "RolloutFailed",
                    "Synthetic failure",
                    true,
                    1,
                    DateTime.UtcNow,
                    DateTime.UtcNow)
            };
        if (state == "applied-without-rollout-proof")
            deployment = deployment with
            {
                State = new AppliedResourceDeploymentState(deployment.DesiredGeneration, DateTime.UtcNow),
                ObservedGeneration = deployment.DesiredGeneration
            };
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id, deployment);
        var retired = await Apply(Snapshot(keys: ["NEXT_TOKEN"]), commit: "retire-reference");
        Assert.Equal(RepositoryPipelineStatus.Blocked, retired.Status);
        Assert.Contains(retired.Operations,
            item => item.Failure is SecretDeclarationRejected
            {
                Reason: SecretDeclarationError.SecretKeysInUse
            });
        var secret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        Assert.Contains("API_TOKEN", secret.KeyNames);
    }

    [Fact]
    public async Task Missing_values_never_launch_then_first_empty_value_resumes_same_commit_and_replay_is_noop()
    {
        var blocked = await Apply();
        Assert.True(blocked.Snapshot.Valid, JsonSerializer.Serialize(blocked.Snapshot.Diagnostics));
        Assert.Equal(RepositoryPipelineStatus.Blocked, blocked.Status);
        Assert.Contains(blocked.Operations,
            item => item.Failure is WebServiceSecretReferenceRejected
            {
                Error.Reason: ConsumerSecretReferenceError.SecretValuesMissing
            });
        var service = await Service();
        Assert.IsType<PreparedWebServiceLifecycle>(service.Lifecycle);
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        Assert.IsType<PreparedResourceDeploymentState>(placement.State);
        Assert.Equal(0, await Releases());
        Assert.Equal(0,
            await Fixture.Database.GetCollection<SecretVersion>("SecretVersions")
                .CountDocumentsAsync(Builders<SecretVersion>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        await Services.GetRequiredService<WakeResourceDeploymentsForRuntimeCell>()(service.TenantId,
            placement.RuntimeCellId,
            10,
            DateTime.UtcNow);
        await Services.GetRequiredService<RecoverResourceDeployment>()(placement.Id, DateTime.UtcNow.AddDays(1));
        Assert.IsType<PreparedResourceDeploymentState>(
            (await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync()).State);
        Assert.Equal(0, await Releases());
        await Supply();
        var resumed = await Pipeline.ResumeAsync(blocked.Context!);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, resumed.Status);
        var active = await Service();
        Assert.Equal(service.Id, active.Id);
        Assert.IsType<ActiveWebServiceLifecycle>(active.Lifecycle);
        Assert.Equal(1, await Releases());
        var version = (await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync()).CurrentVersion;
        Assert.Equal(1, version);
        var before = await DbContext.ResourceDeployments.Find(item => item.ResourceId == active.Id).SingleAsync();
        var outbox = await DbContext.OutboxMessages.CountDocumentsAsync(
            Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Eq(item => item.TenantId, Data.TenantId) & Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Ne(
                item => item.EventType,
                "pole.gitops/v1/source-work-requested"));
        var replay = await Apply();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, replay.Status);
        Assert.Equal(active.Revision, (await Service()).Revision);
        Assert.Equal(1, await Releases());
        Assert.Equal(before.DesiredGeneration,
            (await DbContext.ResourceDeployments.Find(item => item.Id == before.Id).SingleAsync()).DesiredGeneration);
        Assert.Equal(outbox,
            await DbContext.OutboxMessages.CountDocumentsAsync(
                Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Eq(item => item.TenantId, Data.TenantId) & Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Ne(
                    item => item.EventType,
                    "pole.gitops/v1/source-work-requested")));
    }

    [Fact]
    public async Task Image_update_keeps_identity_and_blocked_new_configuration_preserves_last_working_release()
    {
        var initial = await Apply();
        await Supply("synthetic");
        await Pipeline.ResumeAsync(initial.Context!);
        var before = await Service();
        var imageUpdate = await Apply(Snapshot("2"), commit: "image-commit");
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, imageUpdate.Status);
        var updated = await Service();
        Assert.Equal(before.Id, updated.Id);
        Assert.Equal("2", updated.Spec.Image.ResolvedImage!.Tag);
        Assert.Equal(2, await Releases());
        var blocked = await Apply(Snapshot("3", ["API_TOKEN", "SECOND_TOKEN"]), commit: "blocked-commit");
        Assert.Equal(RepositoryPipelineStatus.Blocked, blocked.Status);
        Assert.Contains(blocked.Operations,
            item => item.Failure is WebServiceSecretReferenceRejected
            {
                Error.Reason: ConsumerSecretReferenceError.SecretValuesMissing
            });
        Assert.Equal("2", (await Service()).Spec.Image.ResolvedImage!.Tag);
        Assert.Equal(updated.ActiveRevision, (await Service()).ActiveRevision);
        var state = await Services.GetRequiredService<GitRepositorySources>()
            .StateAsync(Data.TenantId, source, default);
        Assert.Equal(blocked.Context!.Authority.Commit, state.Commits.Observed);
        Assert.Equal(blocked.Context.Authority.Commit, state.Commits.Attempted);
        Assert.Equal(imageUpdate.Context!.Authority.Commit, state.Commits.FullyApplied);
        Assert.Equal(GitRunCompletion.Blocked,
            Assert.IsType<GitRunFinished>((await Pipeline.LatestAsync(source)).State)
                .Completion);
        Assert.Equal(2, await Releases());
        await Supply("synthetic-next");
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Pipeline.ResumeAsync(blocked.Context!)).Status);
        Assert.Equal("3", (await Service()).Spec.Image.ResolvedImage!.Tag);
    }

    [Theory]
    [InlineData("identity-to-alias", false)]
    [InlineData("alias-to-alias", false)]
    [InlineData("alias-to-identity", false)]
    [InlineData("identity-to-alias", true)]
    [InlineData("alias-to-alias", true)]
    [InlineData("alias-to-identity", true)]
    public async Task In_use_projected_alias_is_preserved_even_when_replacement_configuration_is_blocked(
        string transition,
        bool missingReplacementValue)
    {
        var previousAlias = transition == "identity-to-alias" ? "API_TOKEN" : "OLD";
        var nextAlias = transition == "alias-to-identity" ? "API_TOKEN" : "NEW";
        var initial = await Apply(files: SnapshotWithAliases(previousAlias));
        await Supply("synthetic");
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Pipeline.ResumeAsync(initial.Context!)).Status);
        var before = await Service();
        var beforeSecret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        var previousBinding = Assert.Single(beforeSecret.Bindings);
        Assert.Equal(["API_TOKEN"], previousBinding.Pins.Required.ToArray());
        var bindingEvents = await DbContext.OutboxMessages.CountDocumentsAsync(item =>
            item.TenantId == Data.TenantId && item.EventType == "pole.secrets/v1/secret-changed" && item.PayloadJson.Contains("binding-added"));
        Assert.True(bindingEvents > 0);

        var blocked = await Apply(SnapshotWithAliases(nextAlias, missingReplacementValue), commit: "alias-change");
        Assert.Equal(RepositoryPipelineStatus.Blocked, blocked.Status);
        Assert.Contains(blocked.Operations,
            item => item.Failure is SecretBindingDeclarationRejected
            {
                Reason: SecretBindingDeclarationError.SecretKeysInUse
            });
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Pipeline.ResumeAsync(blocked.Context!)).Status);
        var retained = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        var binding = Assert.Single(retained.Bindings);
        Assert.Equal(previousBinding.KeyMappings.OrderBy(pair => pair.Key).ToArray(),
            binding.KeyMappings.OrderBy(pair => pair.Key).ToArray());
        Assert.Equal(previousBinding.Pins.Required.ToArray(), binding.Pins.Required.ToArray());
        var projection = SecretProjectionFactory.Create(retained, binding, "placement", "cell", "target", "namespace");
        var manifest = SecretProjectionManifest.Create(projection,
            binding,
            new Dictionary<string, string> { ["API_TOKEN"] = "synthetic" });
        Assert.Contains(previousAlias, manifest.Data.Keys);
        Assert.DoesNotContain(nextAlias, manifest.Data.Keys);
        Assert.Equal(before.ActiveRevision, (await Service()).ActiveRevision);
        Assert.Equal(1, await Releases());
        Assert.Equal(bindingEvents,
            await DbContext.OutboxMessages.CountDocumentsAsync(item =>
                item.TenantId == Data.TenantId && item.EventType == "pole.secrets/v1/secret-changed" && item.PayloadJson.Contains("binding-added")));
    }

    [Fact]
    public async Task Equivalent_projected_aliases_and_unused_key_remapping_remain_available()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Apply(SnapshotWithAliases("API_TOKEN", explicitIdentity: true))).Status);
        Assert.Equal(1, await Releases());
        var files = SnapshotWithAliases("API_TOKEN", explicitIdentity: true).Select(file => file with
        {
            Content = string.Join("\n---\n",
                file.Content.Split("\n---\n").Select(document =>
                {
                    var node = JsonNode.Parse(document)!;
                    if (node["kind"]!.GetValue<string>() == "WebService")
                        node["spec"]!["secretEnvironment"] = new JsonObject();
                    return node.ToJsonString();
                }))
        }).ToArray();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        Assert.Empty(Assert.Single((await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync()).Bindings).Pins
            .Required);
        files = files.Select(file => file with
        {
            Content = file.Content.Replace("\"API_TOKEN\":\"API_TOKEN\"", "\"API_TOKEN\":\"NEW\"")
        }).ToArray();
        var premature = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.Blocked, premature.Status);
        var retainedSecret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        var deployment = await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Eq(item => item.TenantId, Data.TenantId))
            .SingleAsync();
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == deployment.Id,
            deployment with
            {
                State = new AppliedResourceDeploymentState(deployment.DesiredGeneration, DateTime.UtcNow),
                ObservedGeneration = deployment.DesiredGeneration
            });
        Assert.True(await Services.GetRequiredService<ReleaseRetainedSecretKeys>()(retainedSecret.TenantId,
            retainedSecret.Id,
            retainedSecret.Revision,
            retainedSecret.ReferenceFence,
            Assert.Single(retainedSecret.Bindings).Id,
            [new(deployment.Id, deployment.Revision, deployment.DesiredGeneration)],
            default));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Pipeline.ResumeAsync(premature.Context!)).Status);
        Assert.Equal("NEW",
            Assert.Single((await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync()).Bindings)
                .KeyMappings["API_TOKEN"]);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("secret-values")]
    [InlineData("whitespace-tag")]
    [InlineData("duplicate-binding")]
    public async Task Invalid_entire_snapshot_has_no_writes(string scenario)
    {
        var valid = Snapshot()[0];
        var invalid = scenario switch
        {
            "malformed" => "kind: [",
            "duplicate" => valid.Content,
            "duplicate-binding" =>
                "apiVersion: pole.io/v1alpha1\nkind: SecretBinding\nmetadata: {name: other-access}\n" +
                "spec: {secret: demo-api-credentials, consumer: {kind: WebService, name: demo-api}}",
            "whitespace-tag" =>
                "apiVersion: pole.io/v1alpha1\nkind: ResourceGroup\nmetadata: {name: zzz}\nspec: {tags: [' ']}",
            _ =>
                "apiVersion: pole.io/v1alpha1\nkind: Secret\nmetadata: {name: forbidden}\nspec: {resourceGroup: demo, keys: [TOKEN], values: {TOKEN: plaintext}}"
        };
        var result = await Apply(files: [valid, new("invalid.yaml", invalid)]);
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, result.Status);
        Assert.Equal(0,
            await DbContext.ResourceGroups.CountDocumentsAsync(
                Builders<Pole.Features.ResourceGroups.Domain.ResourceGroup>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        Assert.Equal(0, await Inventory.CountDocumentsAsync(Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        await AssertNoAcceptedSnapshotAsync();
    }

    [Fact]
    public async Task Stale_execution_and_other_source_cannot_change_accepted_owned_configuration()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var newer = await Apply(Snapshot("2"));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, newer.Status);
        Assert.Equal(RepositoryPipelineStatus.StaleExecution, (await Pipeline.ResumeAsync(initial.Context!)).Status);
        var otherSource = Id();
        var conflict = await Apply(files: Snapshot("foreign"), sourceId: otherSource);
        Assert.Contains(conflict.Operations, item => item.Failure is GitDeclarationOwnershipConflict);
        var receipts = await Fixture.Database.GetCollection<GitOperationReceipt>("GitOperationReceipts")
            .Find(item => item.SourceId == otherSource).ToListAsync();
        Assert.Contains(receipts, item => item.Blocker == "OwnershipConflict");
        Assert.All(await Inventory.Find(item => item.TenantId == Data.TenantId && item.Kind == "WebService").ToListAsync(),
            item => Assert.Equal(source, item.SourceId));
        Assert.Equal("2", (await Service()).Spec.Image.ResolvedImage!.Tag);
    }

    [Fact]
    public async Task Concurrent_resumes_and_restart_replay_preserve_prepared_ids_and_single_release()
    {
        var initial = await Apply();
        await Supply();
        var results =
            await Task.WhenAll(Pipeline.ResumeAsync(initial.Context!), Pipeline.ResumeAsync(initial.Context!));
        // One contender may observe a stale provider revision; a subsequent pass must converge.
        Assert.All(results,
            result => Assert.DoesNotContain(result.Operations,
                item => item.Failure is GitDeclarationOwnershipConflict));
        using var restarted = TestServiceProviderFactory.Create(Fixture, ConfigureServices);
        using var scope = restarted.CreateScope();
        var resumed = await new RepositoryPipelineDriver(scope.ServiceProvider, Fixture, Data, repository)
            .RecoverAsync(new(Data.TenantId, source));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, resumed.Status);
        Assert.Equal(1, await WebServices.CountDocumentsAsync(Builders<WebService>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        Assert.Equal(1, await Releases());
        Assert.Equal(10,
            await Fixture.Database.GetCollection<GitOperationReceipt>("GitOperationReceipts")
                .CountDocumentsAsync(Builders<GitOperationReceipt>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }

    [Fact]
    public async Task Manual_updates_and_centrally_persisted_image_changes_are_rejected_with_no_outbox_or_release()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var current = await Service();
        var outbox = await DbContext.OutboxMessages.CountDocumentsAsync(
            Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Eq(item => item.TenantId, Data.TenantId) & Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Ne(
                item => item.EventType,
                "pole.gitops/v1/source-work-requested"));
        var result = await Mediator.Send(new UpdateWebServiceRequest(current.Id,
            new(current.Spec.ToDto() with { Image = new("docker.io", "nginx", "manual") },
                [],
                WebServiceExposureMode.Private,
                current.ActiveRevision),
            current.TenantId));
        var error = Assert.IsType<UpdateWebServiceError>(result.AssertHandlerOutcome().Value);
        Assert.Equal(WebServiceUpdateConflictReason.GitOwnedConfiguration,
            Assert.IsType<WebServiceUpdateConflict>(error.Value).Reason);
        var groupUpdate = await Mediator.Send(new UpdateResourceGroupRequest(current.ResourceGroupId,
            new("manual change", []),
            current.TenantId));
        var groupError = Assert.IsType<UpdateResourceGroupError>(groupUpdate.AssertHandlerOutcome().Value);
        Assert.Equal(ResourceGroupRevisionConflictReason.GitOwnedConfiguration,
            Assert.IsType<ResourceGroupRevisionConflict>(groupError.Value).Reason);
        Assert.Equal(current.Revision, (await Service()).Revision);
        Assert.Equal(1, await Releases());
        Assert.Equal(outbox,
            await DbContext.OutboxMessages.CountDocumentsAsync(
                Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Eq(item => item.TenantId, Data.TenantId) & Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Ne(
                    item => item.EventType,
                    "pole.gitops/v1/source-work-requested")));
        var secret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        Assert.IsType<SecretGitOwned>(
            (await Services.GetRequiredService<DeleteStoredSecret>()(current.TenantId, secret.Id, default)).Value);
        Assert.IsType<SecretGitOwned>(
            (await Services.GetRequiredService<RemoveSecretBinding>()(current.TenantId,
                secret.Id,
                secret.Bindings.Single().Id,
                default)).Value);
    }

    [Fact]
    public async Task Legacy_id_placement_fields_are_rejected_before_any_snapshot_writes()
    {
        var files = Snapshot().Select(file => file with
        {
            Content = file.Content
                .Replace("\"deploymentTarget\":\"lab\"",
                    "\"deploymentTargetId\":\"" + TestData.DefaultDeploymentTargetId + "\"")
                .Replace("\"name\":\"small\"", "\"id\":\"" + TestData.DefaultComputeProfileId + "\"")
        }).ToArray();
        var result = await Apply(files: files);
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, result.Status);
        Assert.Equal(0, await Inventory.CountDocumentsAsync(Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        await AssertNoAcceptedSnapshotAsync();
        Assert.Equal(0, await WebServices.CountDocumentsAsync(Builders<WebService>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }

    [Fact]
    public async Task Source_build_without_authority_and_image_token_paths_cannot_change_Git_image_or_create_release()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var current = await Service();
        var before = await DbContext.ResourceDeployments.Find(item => item.ResourceId == current.Id).SingleAsync();
        var outboxCount = await DbContext.OutboxMessages.CountDocumentsAsync(
            Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Eq(item => item.TenantId, Data.TenantId) & Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Ne(
                item => item.EventType,
                "pole.gitops/v1/source-work-requested"));
        var build = await Services.GetRequiredService<DeploySourceBuildWebService>()(new(current.TenantId,
            current.Id,
            Id(),
            Id(),
            new string('a', 40),
            new(Id(), 1, 1, "unclaimed", 1),
            Data.UserId,
            Data.UserEmail,
            new("docker.io", "nginx", "build", "sha256:" + new string('d', 64), "synthetic-pull")));
        var buildError = Assert.IsType<SourceBuildWebServiceDeploymentError>(build.Value);
        Assert.Equal(SourceBuildWebServiceUpdateConflictReason.SupersededBuild,
            Assert.IsType<SourceBuildWebServiceUpdateConflict>(buildError.Value).Reason);
        var issued = WebServiceDeploymentTokenFactory.Issue(current.TenantId,
            current.Id,
            "synthetic-test",
            Data.UserId,
            Data.UserEmail,
            WebServiceDeploymentTokenFactory.DefaultOptions);
        await Services.GetRequiredService<PersistIssuedWebServiceDeploymentToken>()(issued.Token);
        var handler =
            ActivatorUtilities.CreateInstance<UpdateWebServiceImageTagHandler>(Services, new TenantContextAccessor());
        var tokenUpdate = await handler.HandleAsync(new(current.Id,
            new("token", "test"),
            Authorization: $"Bearer {issued.RawToken}"));
        var tokenError = Assert.IsType<UpdateWebServiceImageTagError>(tokenUpdate.Value);
        Assert.Equal(WebServiceUpdateConflictReason.GitOwnedConfiguration,
            Assert.IsType<WebServiceUpdateConflict>(tokenError.Value).Reason);
        var token = await Fixture.Database.GetCollection<WebServiceDeploymentToken>("WebServiceDeploymentTokens")
            .Find(item => item.Id == issued.Token.Id).SingleAsync();
        Assert.Null(token.LastUsedAtUtc);
        Assert.Equal(current.Revision, (await Service()).Revision);
        Assert.Equal(1, await Releases());
        Assert.Equal(before.DesiredGeneration,
            (await DbContext.ResourceDeployments.Find(item => item.Id == before.Id).SingleAsync()).DesiredGeneration);
        Assert.Equal(outboxCount,
            await DbContext.OutboxMessages.CountDocumentsAsync(
                Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Eq(item => item.TenantId, Data.TenantId) & Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Ne(
                    item => item.EventType,
                    "pole.gitops/v1/source-work-requested")));
    }

    [Fact]
    public async Task
        Provider_transaction_aborts_resource_ownership_receipt_and_outbox_then_recovers_allocated_identity()
    {
        var failingProvider = ActivatorUtilities.CreateInstance<DeclarativeResourceGroupProvider>(Services,
            (StoreOutboxWrites)(async (session, writes, ct) =>
            {
                await Services.GetRequiredService<StoreOutboxWrites>()(session, writes, ct);
                throw new InjectedOutboxFailure();
            }));
        var registry = new DeclarativeProviderRegistry([failingProvider]);
        var failing = new RepositoryPipelineDriver(Services, Fixture, Data, repository, registry);
        await Assert.ThrowsAsync<InjectedOutboxFailure>(() => failing.SubmitAsync(new(Data.TenantId, source),
        [
            new("group.yaml", "apiVersion: pole.io/v1alpha1\nkind: ResourceGroup\nmetadata: {name: demo}\nspec: {}")
        ]));
        Assert.Equal(0, await Inventory.CountDocumentsAsync(Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        Assert.Equal(0,
            await DbContext.ResourceGroups.CountDocumentsAsync(
                Builders<Pole.Features.ResourceGroups.Domain.ResourceGroup>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        Assert.Equal(0,
            await DbContext.OutboxMessages.CountDocumentsAsync(
                Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Eq(item => item.TenantId, Data.TenantId) & Builders<Pole.Features.Outbox.Domain.OutboxMessage>.Filter.Ne(
                    item => item.EventType,
                    "pole.gitops/v1/source-work-requested")));
        Assert.Equal(0,
            await Fixture.Database.GetCollection<GitOperationReceipt>("GitOperationReceipts")
                .CountDocumentsAsync(Builders<GitOperationReceipt>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        var run = await Pipeline.LatestAsync(source);
        var allocated = Assert.IsType<GitInputAccepted>(run.Input).Snapshot.Resources.Single().ResourceId;
        await Pipeline.MakeRecoveryDueAsync(source);
        var recovered = await Pipeline.RecoverAsync(new(Data.TenantId, source));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, recovered.Status);
        Assert.Equal(allocated,
            (await DbContext.ResourceGroups
                .Find(Builders<Pole.Features.ResourceGroups.Domain.ResourceGroup>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync()).Id);
    }

    [Fact]
    public async Task Same_name_manual_resource_is_not_adopted_and_remains_manually_usable()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("demo"));
        var result = await Apply();
        Assert.Contains(result.Operations,
            item => item.Failure is DeclarationAdoptionRequired);
        var gate = Services.GetRequiredService<IGitOpsMutations>();
        Assert.Null(await gate.StatusAsync(
            new LoadGitResourceStatusRequest(
                TenantId: group.TenantId,
                Kind: "ResourceGroup",
                ResourceId: group.Id),
            ct: default));
        var current = await DbContext.ResourceGroups.Find(item => item.Id == group.Id).SingleAsync();
        Assert.Equal(group.Name, current.Name);
        Assert.Equal(group.Description, current.Description);
    }

    [Fact]
    public async Task In_snapshot_scope_and_key_contradictions_reject_before_source_acceptance()
    {
        var files = Snapshot();
        var missingKey = files[0] with
        {
            Content = files[0].Content.Replace("\"keys\":[\"API_TOKEN\"]", "\"keys\":[\"OTHER\"]")
        };
        var invalid = await Apply(files: [missingKey]);
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, invalid.Status);
        Assert.Contains(invalid.Snapshot.Diagnostics, diagnostic => diagnostic.Code == "ContradictoryReferenceKey");
        var deniedKeys = files[0] with
        {
            Content = files[0].Content.Replace("\"keys\":[\"API_TOKEN\"]", "\"keys\":[\"API_TOKEN\",\"OTHER\"]")
                .Replace("\"consumer\":{\"kind\":\"WebService\",\"name\":\"demo-api\"}",
                    "\"consumer\":{\"kind\":\"WebService\",\"name\":\"demo-api\"}," +
                    "\"keyMappings\":{\"OTHER\":\"OTHER\"}")
        };
        var invalidGrant = await Apply(files: [deniedKeys]);
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, invalidGrant.Status);
        Assert.Contains(invalidGrant.Snapshot.Diagnostics, diagnostic => diagnostic.Code == "ContradictoryAccessKeys");
        Assert.Equal(0, await Inventory.CountDocumentsAsync(Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, Data.TenantId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_source_references_preserve_ownership_and_ready_cell_cannot_launch_missing_values(
        bool missingValues)
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var cell = await DbContext.RuntimeCells
            .Find(Builders<Pole.Features.ResourceGroups.Domain.RuntimeCell>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        await DbContext.RuntimeCells.ReplaceOneAsync(item => item.Id == cell.Id,
            cell.MarkReady(cell.DesiredGeneration, DateTime.UtcNow));
        var declarations = Snapshot()[0].Content.Split("\n---\n")
            .Select(text => System.Text.Json.Nodes.JsonNode.Parse(text)).ToArray();
        var second = new List<string>();
        foreach (var declaration in declarations)
        {
            var kind = declaration!["kind"]!.GetValue<string>();
            if (kind == "WebService")
            {
                declaration["metadata"]!["name"] = "second-api";
                if (missingValues)
                    declaration["spec"]!["secretEnvironment"]!["API_TOKEN"]!["key"] = "MISSING";
                second.Add(declaration.ToJsonString());
            }

            if (kind == "SecretBinding")
            {
                declaration["metadata"]!["name"] = "second-access";
                declaration["spec"]!["consumer"]!["name"] = "second-api";
                second.Add(declaration.ToJsonString());
            }
        }

        var secondSource = Id();
        var applied = await Apply(files: [new("second.yaml", string.Join("\n---\n", second))], sourceId: secondSource);
        Assert.Equal(missingValues ? RepositoryPipelineStatus.Blocked : RepositoryPipelineStatus.ConfigurationApplied,
            applied.Status);
        var secret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        var status = await Services.GetRequiredService<IGitOpsMutations>().StatusAsync(
            new LoadGitResourceStatusRequest(
                TenantId: secret.TenantId,
                Kind: "Secret",
                ResourceId: secret.Id),
            ct: default);
        Assert.Equal(source, status!.SourceId);
        Assert.Equal(2, secret.Bindings.Length);
        var secondService = await WebServices.Find(item => item.TenantId == Data.TenantId && item.Name == "second-api").SingleAsync();
        Assert.NotEqual((await WebServices.Find(item => item.TenantId == Data.TenantId && item.Name == "demo-api").SingleAsync()).Id, secondService.Id);
        Assert.Equal(missingValues ? 1 : 2, await Releases());
        if (missingValues)
        {
            Assert.IsType<PreparedWebServiceLifecycle>(secondService.Lifecycle);
            var prepared = await DbContext.ResourceDeployments.Find(item => item.ResourceId == secondService.Id)
                .SingleAsync();
            Assert.IsType<PreparedResourceDeploymentState>(prepared.State);
            Assert.Equal(cell.Id, prepared.RuntimeCellId);
        }
    }

    [Fact]
    public async Task Removed_declarations_remain_owned_pending_removal_without_pruning()
    {
        var initial = await Apply();
        var removed = await Apply([]);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, removed.Status);
        var retained = await Inventory.Find(Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, Data.TenantId)).ToListAsync();
        Assert.Equal(4, retained.Count);
        Assert.All(retained, item => Assert.True(item.PendingRemoval));
        Assert.Single(await WebServices.Find(Builders<WebService>.Filter.Eq(item => item.TenantId, Data.TenantId)).ToListAsync());
        Assert.Equal(RepositoryPipelineStatus.StaleExecution, (await Pipeline.ResumeAsync(initial.Context!)).Status);
    }

    [Fact]
    public async Task Prepared_placements_remain_inspectable_without_retry_or_private_access()
    {
        var blocked = await Apply();
        Assert.Equal(RepositoryPipelineStatus.Blocked, blocked.Status);
        var service = await Service();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        var operations = await Services
            .GetRequiredService<Pole.Features.ResourceDeployments.Application.Reconciliation.
                ResourceDeploymentOperationsPersistence>()
            .PageAsync(service.TenantId, null, "pole/web-service", service.Id, 1, 10, default);
        var operation = Assert.Single(operations.Items);
        Assert.Equal("Prepared", operation.Phase);
        Assert.False(operation.Retryable);
        Assert.Equal(1, operations.Counts.Waiting);
        var runtime =
            await Services
                .GetRequiredService<
                    Pole.Features.ResourceDeployments.Contracts.LoadExactResourceDeploymentRuntimePlacement>()(
                    new LoadExactResourceDeploymentRuntimePlacementRequest(
                        TenantId: service.TenantId,
                        ResourceType: WebServiceResourceDeployment.ResourceType,
                        ResourceId: service.Id,
                        DeploymentId: placement.Id),
                    cancellationToken: default);
        Assert.Equal(ResourceDeploymentStatus.Prepared, runtime!.Status);
        var access = await Services.GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<
                Pole.Features.ResourceDeployments.Contracts.ResourceDeploymentAccessPlacement>(async (tx, ct) =>
            {
                var result =
                    await Services
                        .GetRequiredService<Pole.Features.ResourceDeployments.Contracts.
                            LoadExactResourceDeploymentAccessPlacement>()(
                            new LoadExactResourceDeploymentAccessPlacementRequest(
                                TenantId: service.TenantId,
                                ResourceType: WebServiceResourceDeployment.ResourceType,
                                ResourceId: service.Id,
                                DeploymentId: placement.Id),
                            session: tx.Session,
                            cancellationToken: ct);
                return tx.Abort(result!);
            });
        Assert.Equal(ResourceDeploymentStatus.Prepared,
            access.Status); // Existing private-access admission accepts Applied only.
        var listed = await Mediator.Send(new GetWebServicesRequest { TenantId = service.TenantId });
        var services =
            Assert.IsType<Pole.Features.Request.PaginatedResponse<WebServiceDto>>(listed.AssertHandlerOutcome().Value);
        Assert.Equal("SecretValuesMissing", Assert.Single(services.Items).GitOps!.Blocker);
        Assert.Empty((await Services.GetRequiredService<IGitOpsMutations>().StatusesAsync(
            new LoadGitResourceStatusesRequest(
                TenantId: Id(),
                Kind: "WebService",
                ResourceIds: [service.Id]),
            ct: default)));
    }

    [Fact]
    public async Task A_late_blocked_attempt_cannot_overwrite_a_newer_successful_resume()
    {
        var initial = await Apply();
        var paused = new PauseAfterBlockedConfigure(Services.GetRequiredService<IGitOpsMutations>());
        var pipeline = new RepositoryPipelineDriver(Services, Fixture, Data, repository, customMutations: paused);
        var oldAttempt = pipeline.ResumeAsync(initial.Context!);
        await paused.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await Supply();
            await Pipeline.MakeRecoveryDueAsync(source);
            Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
                (await Pipeline.RecoverAsync(new(Data.TenantId, source))).Status);
        }
        finally
        {
            paused.Release.TrySetResult();
        }

        Assert.Equal(RepositoryPipelineStatus.StaleExecution, (await oldAttempt).Status);
        var run = await Pipeline.LatestAsync(source);
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
        var owner = await Inventory.Find(item => item.TenantId == Data.TenantId && item.Kind == "WebService").SingleAsync();
        Assert.Null(owner.Blocker);
        Assert.Equal(owner.DesiredHash, owner.AppliedHash);
        Assert.Equal(1, await Releases());
    }

    [Fact]
    public async Task GitOps_release_records_server_provenance_without_a_fabricated_user()
    {
        var initial = await Apply();
        await Supply();
        var applied = await Pipeline.ResumeAsync(initial.Context!);
        var release = await Fixture.Database.GetCollection<WebServiceRelease>("WebServiceReleases")
            .Find(Builders<WebServiceRelease>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        Assert.Null(release.ActorUserId);
        Assert.Null(release.ActorEmail);
        var provenance = Assert.IsType<GitOpsReleaseProvenance>(release.Provenance);
        Assert.Equal(source, provenance.SourceId);
        Assert.Equal(applied.Context!.RunId, provenance.RunId);
        Assert.Equal(applied.Context.Authority.Commit, provenance.CommitSha);
    }

    [Theory]
    [InlineData("long-port")]
    [InlineData("trailing-dash-port")]
    [InlineData("startup-success")]
    [InlineData("liveness-success")]
    public async Task Kubernetes_shape_errors_are_rejected_before_snapshot_acceptance(string scenario)
    {
        var text = Snapshot()[0].Content;
        var documents = text.Split("\n---\n").Select(item => System.Text.Json.Nodes.JsonNode.Parse(item)!).ToArray();
        var service = documents.Single(item => item["kind"]!.GetValue<string>() == "WebService")["spec"]!;
        if (scenario == "long-port")
            service["ports"]![0]!["name"] = "over-fifteen-chars";
        if (scenario == "trailing-dash-port")
            service["ports"]![0]!["name"] = "http-";
        if (scenario == "startup-success")
            service["probes"]!["startup"]!["successThreshold"] = 2;
        if (scenario == "liveness-success")
            service["probes"]!["liveness"]!["successThreshold"] = 2;
        var result = await Apply(files:
            [new("invalid.yaml", string.Join("\n---\n", documents.Select(item => item.ToJsonString())))]);
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, result.Status);
        Assert.Equal(0, await Inventory.CountDocumentsAsync(Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, Data.TenantId)));
        await AssertNoAcceptedSnapshotAsync();
    }

    [Fact]
    public async Task Completed_execution_cannot_bypass_manual_configuration_guards()
    {
        var initial = await Apply();
        var service = await Service();
        Assert.Equal(Data.TenantId, initial.Context!.TenantId);
        await Supply();
        var resumed = await Pipeline.ResumeAsync(initial.Context);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, resumed.Status);
        var current = await Service();
        var result = await Mediator.Send(new UpdateWebServiceRequest(service.Id,
            new(current.Spec.ToDto() with { Image = new("docker.io", "nginx", "manual-change") },
                [],
                WebServiceExposureMode.Private,
                current.ActiveRevision),
            service.TenantId));
        Assert.Equal(WebServiceUpdateConflictReason.GitOwnedConfiguration,
            Assert.IsType<WebServiceUpdateConflict>(Assert.IsType<UpdateWebServiceError>(
                result.AssertHandlerOutcome().Value).Value).Reason);
    }

    [Fact]
    public async Task Unsupported_stored_schema_is_visible_and_does_not_loop_or_mutate_resources()
    {
        var initial = await Apply();
        var runs = Fixture.Database.GetCollection<GitReconciliationRun>("GitReconciliationRuns");
        var run = await Pipeline.LatestAsync(source);
        var input = Assert.IsType<GitInputAccepted>(run.Input);
        await runs.UpdateOneAsync(item => item.Id == run.Id,
            Builders<GitReconciliationRun>.Update.Set(
                item => item.Input,
                new GitInputAccepted(input.Snapshot with { ApiVersion = "pole.io/unsupported" })));
        var before = (await Service()).Revision;
        Assert.Equal(RepositoryPipelineStatus.Failed, (await Pipeline.ResumeAsync(initial.Context!)).Status);
        var stored = await Pipeline.LatestAsync(source);
        Assert.Equal(GitRunCompletion.Failed, Assert.IsType<GitRunFinished>(stored.State).Completion);
        Assert.Contains(stored.Diagnostics, item => item.Code == "StoredSnapshotInvalid");
        Assert.Equal(before, (await Service()).Revision);
    }
}


[Collection(IntegrationTestCollection.Global2)]
public sealed class DeclarativePipelineCoreGlobalTests(MongoReplicaSetFixture fixture) : IsolatedDeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task Pinned_profile_replay_preserves_snapshot_and_explicit_revision_adopts_history()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var profiles = Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        var current = await profiles.Find(item => item.Id == TestData.DefaultComputeProfileId).SingleAsync();
        var replacement = current with
        {
            Revision = current.Revision + 1,
            Resources = ComputeProfileResourceRequirements.Create(
                ComputeProfileResourceQuantity.Create(500, 512),
                ComputeProfileResourceQuantity.Create(1000, 1024))
        };
        Assert.IsType<ComputeProfile>(
            (await Services.GetRequiredService<PersistUpdatedComputeProfile>()(current, replacement)).Value);
        var replay = await Apply();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, replay.Status);
        Assert.Equal(1, await Releases());
        var placement = await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Empty)
            .SingleAsync();
        Assert.Equal(1, placement.ResolvedPlacement.Compute.ComputeProfileRevision);
        Assert.Equal(250, placement.ResolvedPlacement.Compute.Requests.CpuMillis);
        var changed = await Apply(Snapshot(profileRevision: 2));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, changed.Status);
        Assert.Equal(500,
            (await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Empty).SingleAsync())
            .ResolvedPlacement.Compute.Requests.CpuMillis);
        var historical = await Apply(Snapshot(profileRevision: 1));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, historical.Status);
        Assert.Equal(250,
            (await DbContext.ResourceDeployments.Find(Builders<ResourceDeployment>.Filter.Empty).SingleAsync())
            .ResolvedPlacement.Compute.Requests.CpuMillis);
        var missing = await Apply(Snapshot(profileRevision: 99));
        Assert.Contains(missing.Operations,
            item => item.Failure is WebServiceDeclarationRejected
            {
                Reason: WebServiceDeclarationError.ComputeProfileRevisionUnavailable
            });
    }

    [Fact]
    public async Task Missing_profile_name_resumes_after_named_creation_without_a_new_commit()
    {
        var files = Snapshot().Select(file => file with
        {
            Content = file.Content
                .Replace("\"name\":\"small\"", "\"name\":\"later\"")
        }).ToArray();
        var blocked = await Apply(files: files);
        Assert.Contains(blocked.Operations,
            item => item.Failure is WebServiceDeclarationRejected
            {
                Reason: WebServiceDeclarationError.PinnedComputeProfileUnavailable
            });
        Assert.Equal(0, await WebServices.CountDocumentsAsync(Builders<WebService>.Filter.Empty));
        var profile = TestData.SeedComputeProfile(Id(), isDefault: false, technicalName: "later");
        Assert.IsType<ComputeProfile>(
            (await Services.GetRequiredService<PersistCreatedComputeProfile>()(profile)).Value);
        var prepared = await Pipeline.ResumeAsync(blocked.Context!);
        Assert.Contains(prepared.Operations,
            item => item.Failure is WebServiceSecretReferenceRejected
            {
                Error.Reason: ConsumerSecretReferenceError.SecretValuesMissing
            });
        Assert.Equal(profile.Id, (await Service()).DeclaredPlacement!.ComputeProfileId);
        await Supply();
        var applied = await Pipeline.ResumeAsync(prepared.Context!);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, applied.Status);
        Assert.NotEqual(blocked.Context!.RunId, applied.Context!.RunId);
        Assert.Equal(blocked.Context.Authority.Commit, applied.Context.Authority.Commit);
        Assert.Equal(1, await Releases());
    }

    [Fact]
    public async Task Recreated_profile_name_never_retargets_an_existing_declaration_binding()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var before = await Service();
        var profiles = Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        await profiles.DeleteOneAsync(
            Builders<ComputeProfile>.Filter.Eq(item => item.Id, TestData.DefaultComputeProfileId));
        var recreated = TestData.SeedComputeProfile(Id(), isDefault: false) with
        {
            TechnicalName = NamedCatalogTestData.ProfileName,
            Revision = 99
        };
        await profiles.InsertOneAsync(recreated);
        var result = await Apply(Snapshot(tag: "2", profileRevision: 99));
        Assert.Contains(result.Operations,
            item => item.Failure is WebServiceDeclarationRejected
            {
                Reason: WebServiceDeclarationError.PinnedComputeProfileUnavailable
            });
        var retained = await Service();
        Assert.Equal(before.Id, retained.Id);
        Assert.Equal(before.Revision, retained.Revision);
        Assert.Equal(TestData.DefaultComputeProfileId, retained.DeclaredPlacement!.ComputeProfileId);
        Assert.NotEqual(recreated.Id, retained.DeclaredPlacement.ComputeProfileId);
        Assert.Equal(1, await Releases());
    }
}
