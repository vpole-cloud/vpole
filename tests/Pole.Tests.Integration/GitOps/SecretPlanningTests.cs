// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class SecretPlanningTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task Secret_planning_ignores_value_rotation_and_reports_metadata_drift()
    {
        await Apply();
        var declaration = Services.GetRequiredService<ManifestCompiler>()
            .Compile(Snapshot())
            .Declarations
            .Single(item => item.Address.Kind == "Secret");
        var planner = Services.GetRequiredService<DeclarativeResourcePlanner>();
        var transactions = Services.GetRequiredService<MongoRunTransaction>();

        Task<ResourceDeclarationPlan> PlanAsync(PlanDeclarationRequest input) =>
            transactions.InvokeOutcomeAsync<ResourceDeclarationPlan>(async (tx, ct) =>
                tx.Commit(await planner.PlanAsync(input, tx.Session, ct)));

        var request = new PlanDeclarationRequest(
            new DeclarationPlanningScope(Data.TenantId, source, null),
            declaration);
        var before = Assert.IsType<NoOpDeclarationPlan>(await PlanAsync(request));
        await Supply("must-never-appear-in-a-plan");
        var after = Assert.IsType<NoOpDeclarationPlan>(await PlanAsync(request));
        Assert.True(
            JsonElement.DeepEquals(before.Current.Projection.Configuration, after.Current.Projection.Configuration));
        Assert.DoesNotContain("must-never-appear", after.Current.Projection.Configuration.GetRawText());
        Assert.DoesNotContain("currentVersion", after.Current.Projection.Configuration.GetRawText());
        var secret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        await Secrets.ReplaceOneAsync(
            item => item.Id == secret.Id,
            secret with
            {
                Purpose = "drift",
                Revision = secret.Revision + 1,
            });
        Assert.IsType<UpdateDeclarationPlan>(await PlanAsync(request));
        Assert.IsType<BlockedDeclarationPlan>(
            await PlanAsync(
                request with
                {
                    Scope = new DeclarationPlanningScope(Id(), source, null),
                }));
        await Secrets.DeleteOneAsync(item => item.Id == secret.Id);
        Assert.IsType<CreateDeclarationPlan>(await PlanAsync(request));
    }
}
