// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class SecretBindingPlanningTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Fact]
    public async Task Binding_planning_tracks_mapping_and_parent_revision_but_excludes_rollout_pins()
    {
        await Apply();
        var declaration = Services.GetRequiredService<ManifestCompiler>()
            .Compile(Snapshot())
            .Declarations
            .Single(item => item.Address.Kind == "SecretBinding");
        using var session = await Fixture.Database.Client.StartSessionAsync();
        var planner = Services.GetRequiredService<DeclarativeResourcePlanner>();
        var request = new PlanDeclarationRequest(
            new DeclarationPlanningScope(Data.TenantId, source, null),
            declaration);
        var before = Assert.IsType<NoOpDeclarationPlan>(await planner.PlanAsync(request, session, default));
        var secret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId)).SingleAsync();
        var binding = Assert.Single(secret.Bindings);
        Assert.Equal(2, before.Current.Version.Documents.Count);
        Assert.Contains(
            new DeclarationDocumentVersion($"Secret/{secret.Id}", secret.Revision),
            before.Current.Version.Documents);
        Assert.Contains(
            new DeclarationDocumentVersion($"Secret/{secret.Id}/ReferenceFence", secret.ReferenceFence),
            before.Current.Version.Documents);
        await Secrets.ReplaceOneAsync(
            item => item.Id == secret.Id,
            secret with
            {
                Bindings = [binding with { Pins = new SecretKeyPins(["API_TOKEN"], ["PREVIOUS_TOKEN"]) }],
                Revision = secret.Revision + 1,
            });
        var pinned = Assert.IsType<NoOpDeclarationPlan>(await planner.PlanAsync(request, session, default));
        Assert.DoesNotContain("PREVIOUS_TOKEN", pinned.Current.Projection.Configuration.GetRawText());
        await Secrets.ReplaceOneAsync(
            item => item.Id == secret.Id,
            secret with
            {
                Bindings =
                [
                    binding with
                    {
                        KeyMappings = ImmutableDictionary<string, string>.Empty.Add("API_TOKEN", "TOKEN"),
                    },
                ],
                Revision = secret.Revision + 2,
            });
        Assert.IsType<UpdateDeclarationPlan>(await planner.PlanAsync(request, session, default));
        await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret with { Bindings = [] });
        Assert.IsType<CreateDeclarationPlan>(await planner.PlanAsync(request, session, default));
        Assert.IsType<BlockedDeclarationPlan>(
            await planner.PlanAsync(
                request with
                {
                    Scope = new DeclarationPlanningScope(Id(), source, null),
                },
                session,
                default));
    }
}
