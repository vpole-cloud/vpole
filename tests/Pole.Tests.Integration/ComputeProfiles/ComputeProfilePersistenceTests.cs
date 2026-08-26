using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Migrations;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Application.Migrations;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.ResourceDeployments.Application.Migrations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ComputeProfiles;

[Collection(IntegrationTestCollection.Name)]
public sealed class ComputeProfilePersistenceTests(MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_promotes_one_default_and_registers_target_references()
    {
        await PrepareDatabaseAsync();
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var persist = host.GetRequiredService<PersistCreatedComputeProfile>();
        var first = ComputeProfileTestData.CreateProfile(
            firstTarget.Id,
            isDefault: true,
            displayName: "First default");
        var second = ComputeProfileTestData.CreateProfile(
            secondTarget.Id,
            isDefault: true,
            displayName: "Second default");

        (await persist(first)).AssertSuccess();
        (await persist(second)).AssertSuccess();

        var persisted = await fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .Find(AllComputeProfiles())
            .ToListAsync();
        var oldDefault = Assert.Single(persisted, profile => profile.Id == first.Id);
        var currentDefault = Assert.Single(persisted, profile => profile.Id == second.Id);
        Assert.False(oldDefault.IsDefault);
        Assert.Equal(2, oldDefault.Revision);
        Assert.True(currentDefault.IsDefault);
        Assert.Equal(1, currentDefault.Revision);

        var references = await fixture.DbContext.DeploymentTargetReferences
            .Find(ComputeProfileReferences())
            .ToListAsync();
        Assert.Equal(2, references.Count);
        Assert.Contains(references, reference =>
            reference.DeploymentTargetId == firstTarget.Id &&
            reference.OwnerId == first.Id);
        Assert.Contains(references, reference =>
            reference.DeploymentTargetId == secondTarget.Id &&
            reference.OwnerId == second.Id);
    }

    [Fact]
    public async Task Update_diffs_target_references_and_rejects_stale_revision()
    {
        await PrepareDatabaseAsync();
        var firstTarget = ComputeProfileTestData.CreateTarget();
        var secondTarget = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertManyAsync([firstTarget, secondTarget]);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var update = host.GetRequiredService<PersistUpdatedComputeProfile>();
        var current = (await create(ComputeProfileTestData.CreateProfile(firstTarget.Id)))
            .AssertSuccess();
        var replacement = Reconfigure(current, secondTarget.Id, "Moved");

        var updated = (await update(current, replacement)).AssertSuccess();

        var references = await fixture.DbContext.DeploymentTargetReferences
            .Find(ComputeProfileReferences(current.Id))
            .ToListAsync();
        var reference = Assert.Single(references);
        Assert.Equal(secondTarget.Id, reference.DeploymentTargetId);

        var staleReplacement = Reconfigure(current, secondTarget.Id, "Stale");
        var stale = await update(current, staleReplacement);
        Assert.IsType<ComputeProfileRevisionConflict>(stale.AssertError().Value);
        var persisted = await fixture.Database
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
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        ApplyDeploymentTargetReferenceChanges rejectReferences =
            (owner, added, removed, session, cancellationToken) =>
                Task.FromResult<Result<Unit, DeploymentTargetReferenceChangeRejected>>(
                    new Err<DeploymentTargetReferenceChangeRejected>(
                        new DeploymentTargetReferenceChangeRejected(
                            owner,
                            added.Concat(removed).ToArray())));
        using var host = new ComputeProfilePersistenceTestHost(
            fixture,
            rejectReferences);
        var profile = ComputeProfileTestData.CreateProfile(target.Id);

        var result = await host.GetRequiredService<PersistCreatedComputeProfile>()(profile);

        var error = result.AssertError();
        Assert.IsType<DeploymentTargetReferenceChangeRejected>(error.Value);
        Assert.Equal(0, await fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .CountDocumentsAsync(ComputeProfileById(profile.Id)));
        Assert.Equal(0, await fixture.DbContext.DeploymentTargetReferences
            .CountDocumentsAsync(ComputeProfileReferences(profile.Id)));
    }

    [Fact]
    public async Task Delete_rejects_default_and_unregisters_nondefault_target_references()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var create = host.GetRequiredService<PersistCreatedComputeProfile>();
        var update = host.GetRequiredService<PersistUpdatedComputeProfile>();
        var delete = host.GetRequiredService<DeleteComputeProfile>();
        var defaultProfile = (await create(ComputeProfileTestData.CreateProfile(
            target.Id,
            isDefault: true,
            displayName: "Default"))).AssertSuccess();
        var other = (await create(ComputeProfileTestData.CreateProfile(
            target.Id,
            displayName: "Other"))).AssertSuccess();

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
        Assert.IsType<DefaultComputeProfileCannotBeDemoted>(
            (await update(defaultProfile, demotion)).AssertError().Value);

        var defaultDelete = await delete(defaultProfile.Id, defaultProfile.Revision);
        AssertDeleteError<DefaultComputeProfileCannotBeDeleted>(defaultDelete);
        AssertDeleteSuccess(await delete(other.Id, other.Revision));

        Assert.Equal(0, await fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .CountDocumentsAsync(ComputeProfileById(other.Id)));
        Assert.Equal(0, await fixture.DbContext.DeploymentTargetReferences
            .CountDocumentsAsync(ComputeProfileReferences(other.Id)));
        Assert.Equal(1, await fixture.DbContext.DeploymentTargetReferences
            .CountDocumentsAsync(ComputeProfileReferences(defaultProfile.Id)));
    }

    [Fact]
    public async Task Delete_rejects_a_profile_referenced_by_resource_deployment_snapshot()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var profile = (await host.GetRequiredService<PersistCreatedComputeProfile>()(
            ComputeProfileTestData.CreateProfile(target.Id))).AssertSuccess();
        await fixture.Database.GetCollection<MongoDB.Bson.BsonDocument>("ResourceDeployments")
            .InsertOneAsync(new MongoDB.Bson.BsonDocument
            {
                ["ResolvedPlacement"] = new MongoDB.Bson.BsonDocument
                {
                    ["Compute"] = new MongoDB.Bson.BsonDocument
                    {
                        ["ComputeProfileId"] = MongoDB.Bson.ObjectId.Parse(profile.Id)
                    }
                }
            });

        var result = await host.GetRequiredService<DeleteComputeProfile>()(
            profile.Id,
            profile.Revision);

        AssertDeleteError<ComputeProfileReferencedByResourceDeployment>(result);
        Assert.Equal(1, await fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .CountDocumentsAsync(ComputeProfileById(profile.Id)));
        Assert.Equal(1, await fixture.DbContext.DeploymentTargetReferences
            .CountDocumentsAsync(ComputeProfileReferences(profile.Id)));
    }

    [Fact]
    public async Task Delete_rejects_stale_revision_and_keeps_target_reference()
    {
        await PrepareDatabaseAsync();
        var target = ComputeProfileTestData.CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var host = new ComputeProfilePersistenceTestHost(fixture);
        var profile = (await host.GetRequiredService<PersistCreatedComputeProfile>()(
            ComputeProfileTestData.CreateProfile(target.Id))).AssertSuccess();

        var result = await host.GetRequiredService<DeleteComputeProfile>()(
            profile.Id,
            profile.Revision + 1);

        AssertDeleteError<ComputeProfileRevisionConflict>(result);
        Assert.Equal(1, await fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .CountDocumentsAsync(ComputeProfileById(profile.Id)));
        Assert.Equal(1, await fixture.DbContext.DeploymentTargetReferences
            .CountDocumentsAsync(ComputeProfileReferences(profile.Id)));
    }

    private async Task PrepareDatabaseAsync()
    {
        await new DeploymentTargetReferenceMigration().Up(fixture.Database);
        await new ComputeProfileSchemaValidationMigration().Up(fixture.Database);
        await new ComputeProfileIndexesMigration().Up(fixture.Database);
        await new ResourceDeploymentComputeProfileIndexMigration().Up(fixture.Database);
    }

    private static ComputeProfile Reconfigure(
        ComputeProfile current,
        string targetId,
        string description) =>
        current.Reconfigure(
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

    private static FilterDefinition<ComputeProfile> AllComputeProfiles() =>
        Builders<ComputeProfile>.Filter.Empty;

    private static FilterDefinition<ComputeProfile> ComputeProfileById(string computeProfileId) =>
        Builders<ComputeProfile>.Filter.Eq(profile => profile.Id, computeProfileId);

    private static FilterDefinition<DeploymentTargetReference> ComputeProfileReferences(
        string? computeProfileId = null)
    {
        var filter = Builders<DeploymentTargetReference>.Filter.Eq(
            reference => reference.OwnerType,
            "compute-profile");
        return computeProfileId is null
            ? filter
            : Builders<DeploymentTargetReference>.Filter.And(
                filter,
                Builders<DeploymentTargetReference>.Filter.Eq(
                    reference => reference.OwnerId,
                    computeProfileId));
    }

    private static TError AssertDeleteError<TError>(DeleteComputeProfileOutcome outcome)
        where TError : notnull
    {
        var error = outcome switch
        {
            ComputeProfileDeleted => throw new Xunit.Sdk.XunitException(
                "Expected a delete error, but the compute profile was deleted."),
            DeleteComputeProfileError deleteError => deleteError.Value
        };
        var leafError = error is DeleteComputeProfileStateError stateError
            ? stateError.Value
            : error;
        return Assert.IsType<TError>(leafError);
    }

    private static ComputeProfileDeleted AssertDeleteSuccess(
        DeleteComputeProfileOutcome outcome) =>
        outcome switch
        {
            ComputeProfileDeleted deleted => deleted,
            DeleteComputeProfileError error => throw new Xunit.Sdk.XunitException(
                $"Expected a deleted compute profile, but received " +
                $"'{error.Value.GetType().Name}'.")
        };
}
