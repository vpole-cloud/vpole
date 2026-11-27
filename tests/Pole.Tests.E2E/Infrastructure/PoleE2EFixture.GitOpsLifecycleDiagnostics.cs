// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Tests.E2E.Infrastructure;

public sealed partial class PoleE2EFixture
{
    public async Task<string> WriteGitOpsLifecycleFailureMetadataAsync(
        string source,
        string run,
        CancellationToken ct)
    {
        var database = await GitOpsDatabaseAsync(ct);
        var scope = new BsonDocument
        {
            { "TenantId", Alpha.DefaultTenantId }, { "SourceId", source }, { "RunId", run },
        };
        var receipts = await database.GetCollection<BsonDocument>("GitOperationReceipts")
            .Find(scope)
            .Project(
                new BsonDocument
                {
                    { "Kind", 1 }, { "Name", 1 }, { "ResourceId", 1 }, { "Phase", 1 }, { "Blocker", 1 },
                    { "Lifecycle.Sequence", 1 }, { "Lifecycle.ActionId", 1 }, { "Lifecycle.Step", 1 },
                    { "Lifecycle.Subject", 1 }, { "Lifecycle.Postcondition.Versions", 1 },
                    { "Lifecycle.Postcondition.ConfigurationHash", 1 }, { "Lifecycle.RelatedVersions", 1 },
                    { "Lifecycle.InventoryRevision", 1 },
                })
            .Limit(64)
            .ToListAsync(ct);
        var execution = await database.GetCollection<BsonDocument>("GitReconciliationRuns")
            .Find(
                new BsonDocument
                {
                    { "_id", run }, { "Stamp.Source.TenantId", Alpha.DefaultTenantId },
                    { "Stamp.Source.SourceId", source },
                })
            .Project(
                new BsonDocument
                {
                    { "Stamp", 1 }, { "State", 1 }, { "Diagnostics.Code", 1 },
                    { "Input.Plan.Operations", 1 }, { "Input.Plan.DependencySets", 1 },
                })
            .SingleAsync(ct);
        var inventory = await database.GetCollection<BsonDocument>("ManagedDeclarations")
            .Find(
                new BsonDocument
                {
                    { "TenantId", Alpha.DefaultTenantId }, { "SourceId", source },
                })
            .Project(
                new BsonDocument
                {
                    { "Kind", 1 }, { "Name", 1 }, { "ResourceId", 1 }, { "PendingRemoval", 1 }, { "MutationFence", 1 },
                    { "LastRunId", 1 }, { "LastPhase", 1 },
                })
            .Limit(64)
            .ToListAsync(ct);
        var current = new BsonDocument();
        foreach (var name in new[]
                 {
                     "ResourceGroups", "IsolationScopes", "ResourceRegistrations",
                     "ResourceDeployments", "RuntimeCells",
                     "WebServices", "FunctionApps", "PostgresClusters", "RedisServices", "Secrets",
                     "ResourceConnections", "SourceRepositories", "SourceBuildConfigurations", "DomainZones",
                     "HostnameBindings", "DeploymentTargets", "ComputeProfiles", "StorageProfiles",
                 })
        {
            var tenant = name is "DeploymentTargets" or "ComputeProfiles" or "StorageProfiles"
                ? new BsonDocument()
                : new BsonDocument(
                    "$or",
                    new BsonArray
                    {
                        new BsonDocument("TenantId", Alpha.DefaultTenantId),
                        new BsonDocument("TenantId", ObjectId.Parse(Alpha.DefaultTenantId)),
                    });
            var rows = await database.GetCollection<BsonDocument>(name)
                .Find(tenant)
                .Project(
                    new BsonDocument
                    {
                        { "Revision", 1 }, { "ReferenceFence", 1 }, { "PlanningFence", 1 }, { "RegistrationFence", 1 },
                        { "ObservationFence", 1 }, { "ConfigurationRevision", 1 }, { "ConnectionVersion", 1 },
                        { "MutationVersion", 1 }, { "PlacementFence", 1 }, { "AdmissionFence", 1 },
                        { "ResourceGroupId", 1 }, { "ResourceTypeId", 1 }, { "ResourceId", 1 },
                        { "DesiredGeneration", 1 },
                        { "Generation", 1 }, { "ResourceRevision", 1 }, { "Lifecycle._t", 1 }, { "State._t", 1 },
                        { "State.Generation", 1 }, { "State.DesiredGeneration", 1 },
                        { "State.Operation.Generation", 1 },
                        { "Management._t", 1 }, { "Management.ResourceType", 1 }, { "Management.ResourceId", 1 },
                    })
                .Limit(64)
                .ToListAsync(ct);
            current[name] = new BsonArray(rows);
        }

        var payload = new BsonDocument
        {
            { "Run", execution }, { "Receipts", new BsonArray(receipts) },
            { "Inventory", new BsonArray(inventory) }, { "Current", current },
        };
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "gitops-lifecycle-failures"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ObjectId.Parse(run) + ".json");
        await File.WriteAllTextAsync(path, payload.ToJson(), ct);
        return path;
    }
}
