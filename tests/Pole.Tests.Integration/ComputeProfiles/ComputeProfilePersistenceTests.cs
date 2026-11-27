// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Global3)]
public sealed class ComputeProfilePersistenceTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();

    public override Task DisposeAsync() => base.DisposeAsync();

    [Fact]
    public async Task Create_promotes_one_default_and_registers_target_references()
    {
        await PrepareDatabaseAsync();
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var persist = host.GetRequiredService<PersistCreatedComputeProfile>();
        var first = ComputeProfileTestData.CreateProfile(firstTarget.Id, isDefault: true, displayName: "First default");
        var second = ComputeProfileTestData.CreateProfile(
            secondTarget.Id,
            isDefault: true,
            displayName: "Second default");

        Assert.IsType<ComputeProfile>((await persist(first)).Value);
        Assert.IsType<ComputeProfile>((await persist(second)).Value);

        var persisted = await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .Find(AllComputeProfiles())
            .ToListAsync();
        var oldDefault = Assert.Single(persisted, profile => profile.Id == first.Id);
        var currentDefault = Assert.Single(persisted, profile => profile.Id == second.Id);
        Assert.False(oldDefault.IsDefault);
        Assert.Equal(2, oldDefault.Revision);
        Assert.True(currentDefault.IsDefault);
        Assert.Equal(1, currentDefault.Revision);

        var references
            = await Fixture.DbContext.DeploymentTargetReferences.Find(ComputeProfileReferences()).ToListAsync();
        Assert.Equal(2, references.Count);
        Assert.Contains(
            references,
            reference => reference.DeploymentTargetId == firstTarget.Id && reference.OwnerId == first.Id);
        Assert.Contains(
            references,
            reference => reference.DeploymentTargetId == secondTarget.Id && reference.OwnerId == second.Id);
    }

    [Fact]
    public async Task Update_diffs_target_references_and_rejects_stale_revision()
    {
        await PrepareDatabaseAsync();
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var update = host.GetRequiredService<PersistUpdatedComputeProfile>();
        var currentOutcome = await create(ComputeProfileTestData.CreateProfile(firstTarget.Id));
        var current = Assert.IsType<ComputeProfile>(currentOutcome.Value);
        var replacement = Reconfigure(current, secondTarget.Id, "Moved");

        var updatedOutcome = await update(current, replacement);
        var updated = Assert.IsType<ComputeProfile>(updatedOutcome.Value);

        var references = await Fixture.DbContext
            .DeploymentTargetReferences
            .Find(ComputeProfileReferences(current.Id))
            .ToListAsync();
        var reference = Assert.Single(references);
        Assert.Equal(secondTarget.Id, reference.DeploymentTargetId);

        var staleReplacement = Reconfigure(current, secondTarget.Id, "Stale");
        var stale = await update(current, staleReplacement);
        Assert.IsType<ComputeProfileRevisionConflict>(stale.Value);
        var persisted = await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .Find(ComputeProfileById(current.Id))
            .SingleAsync();
        Assert.Equal(updated.Revision, persisted.Revision);
        Assert.Equal("Moved", persisted.Description);
    }

    [Fact]
    public async Task Create_aborts_without_persisting_the_profile_when_target_reference_change_is_rejected()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        ApplyDeploymentTargetReferenceChanges rejectReferences = (
            request, session,
            cancellationToken) => Task.FromResult(
            new ApplyDeploymentTargetReferenceChangesOutcome(
                new DeploymentTargetReferenceChangeRejected(
                    request.Owner,
                    request.AddedDeploymentTargetIds.Concat(request.RemovedDeploymentTargetIds).ToArray())));
        using var host = new ComputeProfilePersistenceTestHost(Fixture, rejectReferences);
        var profile = ComputeProfileTestData.CreateProfile(target.Id);

        var result = await host.GetRequiredService<PersistCreatedComputeProfile>()(profile);

        Assert.IsType<DeploymentTargetReferenceChangeRejected>(result.Value);
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<ComputeProfile>("ComputeProfiles")
                .CountDocumentsAsync(ComputeProfileById(profile.Id)));
        Assert.Equal(
            0,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                ComputeProfileReferences(profile.Id)));
    }

    [Fact]
    public async Task Delete_rejects_default_and_unregisters_nondefault_target_references()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var update = host.GetRequiredService<PersistUpdatedComputeProfile>();
        var delete = host.GetRequiredService<DeleteComputeProfile>();
        var defaultProfileOutcome = await create(
            ComputeProfileTestData.CreateProfile(target.Id, isDefault: true, displayName: "Default"));
        var defaultProfile = Assert.IsType<ComputeProfile>(defaultProfileOutcome.Value);
        var otherOutcome = await create(ComputeProfileTestData.CreateProfile(target.Id, displayName: "Other"));
        var other = Assert.IsType<ComputeProfile>(otherOutcome.Value);

        var demotion = defaultProfile.Reconfigure(
            defaultProfile.DisplayName,
            defaultProfile.Description,
            defaultProfile.Resources,
            defaultProfile.Architecture,
            defaultProfile.ExtendedResources,
            defaultProfile.AllowedResourceTypes.Select(ResourceTypeId.Create),
            defaultProfile.TenantAccess,
            defaultProfile.TargetMappings,
            defaultProfile.IsEnabled,
            isDefault: false,
            defaultProfile.UpdatedAtUtc.AddMinutes(1));
        Assert.IsType<DefaultComputeProfileCannotBeDemoted>((await update(defaultProfile, demotion)).Value);

        var defaultDelete = await delete(defaultProfile.Id, defaultProfile.Revision);
        Assert.IsType<DefaultComputeProfileCannotBeDeleted>(defaultDelete.Value);
        Assert.IsType<ComputeProfileDeleted>((await delete(other.Id, other.Revision)).Value);

        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<ComputeProfile>("ComputeProfiles")
                .CountDocumentsAsync(ComputeProfileById(other.Id)));
        Assert.Equal(
            0,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(ComputeProfileReferences(other.Id)));
        Assert.Equal(
            1,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                ComputeProfileReferences(defaultProfile.Id)));
    }

    [Fact]
    public async Task Delete_rejects_a_profile_referenced_by_resource_deployment_snapshot()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var profileOutcome = await host.GetRequiredService<PersistCreatedComputeProfile>()(
            ComputeProfileTestData.CreateProfile(target.Id));
        var profile = Assert.IsType<ComputeProfile>(profileOutcome.Value);
        await Fixture.Database
            .GetCollection<BsonDocument>("ResourceDeployments")
            .InsertOneAsync(
                new BsonDocument
                {
                    ["ResolvedPlacement"] = new BsonDocument
                    {
                        ["Compute"] = new BsonDocument
                        {
                            ["ComputeProfileId"] = ObjectId.Parse(profile.Id),
                        },
                    },
                });

        var result = await host.GetRequiredService<DeleteComputeProfile>()(profile.Id, profile.Revision);

        Assert.IsType<ComputeProfileReferencedByResourceDeployment>(result.Value);
        Assert.Equal(
            1,
            await Fixture.Database
                .GetCollection<ComputeProfile>("ComputeProfiles")
                .CountDocumentsAsync(ComputeProfileById(profile.Id)));
        Assert.Equal(
            1,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                ComputeProfileReferences(profile.Id)));
    }

    [Fact]
    public async Task Delete_rejects_stale_revision_and_keeps_target_reference()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(Fixture);
        var profileOutcome = await host.GetRequiredService<PersistCreatedComputeProfile>()(
            ComputeProfileTestData.CreateProfile(target.Id));
        var profile = Assert.IsType<ComputeProfile>(profileOutcome.Value);

        var result = await host.GetRequiredService<DeleteComputeProfile>()(profile.Id, profile.Revision + 1);

        Assert.IsType<ComputeProfileRevisionConflict>(result.Value);
        Assert.Equal(
            1,
            await Fixture.Database
                .GetCollection<ComputeProfile>("ComputeProfiles")
                .CountDocumentsAsync(ComputeProfileById(profile.Id)));
        Assert.Equal(
            1,
            await Fixture.DbContext.DeploymentTargetReferences.CountDocumentsAsync(
                ComputeProfileReferences(profile.Id)));
    }

    [Fact]
    public async Task Default_promotion_batches_legacy_defaults_and_keeps_both_history_snapshots()
    {
        await PrepareDatabaseAsync();
        // This Fixture-only legacy catalog deliberately predates the unique-default index.
        var profiles = Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
        await profiles.Indexes.DropOneAsync("UX_ComputeProfiles_Default");
        var target = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var legacy = Enumerable.Range(0, 129)
            .Select(index => ComputeProfileTestData.CreateProfile(
                target.Id,
                isDefault: true,
                displayName: $"Legacy {index}"))
            .ToArray();
        await profiles.InsertManyAsync(legacy);

        var commands = new ConcurrentQueue<(string Collection, int Count)>();
        var settings = Fixture.Client.Settings.Clone();
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(started =>
        {
            if (started.CommandName == "update")
                commands.Enqueue((started.Command["update"].AsString, started.Command["updates"].AsBsonArray.Count));
        });
        var client = new MongoClient(settings);
        using var host = new ComputeProfilePersistenceTestHost(
            Fixture,
            database: client.GetDatabase(Fixture.Database.DatabaseNamespace.DatabaseName));
        var promoted = ComputeProfileTestData.CreateProfile(target.Id, isDefault: true);
        Assert.IsType<ComputeProfile>((await host.GetRequiredService<PersistCreatedComputeProfile>()(promoted)).Value);

        var demotions = commands.Where(command => command.Collection == "ComputeProfiles").ToArray();
        Assert.Equal(2, demotions.Length);
        Assert.All(demotions, command => Assert.InRange(command.Count, 1, 128));
        var historyCommands = commands.Where(command => command.Collection == "ComputeProfileRevisions").ToArray();
        Assert.Equal(3, historyCommands.Length); // Two demotion batches plus the new profile.
        Assert.All(historyCommands, command => Assert.InRange(command.Count, 1, 256));
        var history = await Fixture.Database
            .GetCollection<ComputeProfileRevision>("ComputeProfileRevisions")
            .Find(Builders<ComputeProfileRevision>.Filter.Empty)
            .ToListAsync();
        Assert.Equal(259, history.Count);
        foreach (var original in legacy)
        {
            Assert.True(
                Assert.Single(history, item => item.ComputeProfileId == original.Id && item.Revision == 1)
                    .Snapshot.IsDefault);
            Assert.False(
                Assert.Single(history, item => item.ComputeProfileId == original.Id && item.Revision == 2)
                    .Snapshot.IsDefault);
        }

        Assert.Equal(
            promoted.Id,
            (await profiles.Find(Builders<ComputeProfile>.Filter.Eq(item => item.IsDefault, true)).SingleAsync()).Id);
    }

    [Fact]
    public async Task Default_promotion_rolls_back_demotions_and_history_if_outbox_fails()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var old = ComputeProfileTestData.CreateProfile(target.Id, isDefault: true);
        await Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles").InsertOneAsync(old);
        using var host = new ComputeProfilePersistenceTestHost(
            Fixture,
            storeOutboxWrites: (
                _, _,
                _) => throw new InvalidOperationException("Injected outbox failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.GetRequiredService<PersistCreatedComputeProfile>()(
                ComputeProfileTestData.CreateProfile(target.Id, isDefault: true)));
        var remaining = await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .Find(AllComputeProfiles())
            .SingleAsync();
        Assert.Equal(old.Id, remaining.Id);
        Assert.Equal(old.Revision, remaining.Revision);
        Assert.True(remaining.IsDefault);
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<ComputeProfileRevision>("ComputeProfileRevisions")
                .CountDocumentsAsync(Builders<ComputeProfileRevision>.Filter.Empty));
    }

    private async Task PrepareDatabaseAsync()
    {
        await new DeploymentTargetReferenceMigration().Up(Fixture.Database);
        await new ComputeProfileSchemaValidationMigration().Up(Fixture.Database);
        await new ComputeProfileIndexesMigration().Up(Fixture.Database);
        await new ResourceDeploymentComputeProfileIndexMigration().Up(Fixture.Database);
    }

    private static ComputeProfile Reconfigure(
        ComputeProfile current,
        string targetId,
        string description) => current.Reconfigure(
        current.DisplayName,
        description,
        current.Resources,
        current.Architecture,
        current.ExtendedResources,
        current.AllowedResourceTypes.Select(ResourceTypeId.Create),
        current.TenantAccess,
        [ComputeProfileTestData.CreateMapping(targetId)],
        current.IsEnabled,
        current.IsDefault,
        current.UpdatedAtUtc.AddMinutes(1));

    private static FilterDefinition<ComputeProfile> AllComputeProfiles() => Builders<ComputeProfile>.Filter.Empty;

    private static FilterDefinition<ComputeProfile> ComputeProfileById(string computeProfileId) =>
        Builders<ComputeProfile>.Filter.Eq(profile => profile.Id, computeProfileId);

    private static FilterDefinition<DeploymentTargetReference> ComputeProfileReferences(string? computeProfileId = null)
    {
        var filter = Builders<DeploymentTargetReference>.Filter.Eq(reference => reference.OwnerType, "compute-profile");
        return computeProfileId is null
            ? filter
            : Builders<DeploymentTargetReference>.Filter.And(
                filter,
                Builders<DeploymentTargetReference>.Filter.Eq(reference => reference.OwnerId, computeProfileId));
    }
}
