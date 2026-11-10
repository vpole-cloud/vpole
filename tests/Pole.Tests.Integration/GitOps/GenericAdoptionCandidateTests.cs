using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Inventory;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitLifecyclePreviewProjectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Candidate_read_preserves_same_source_allocated_adoption_without_stealing_other_reservations(
        bool sameSource)
    {
        var owner = await Create();
        var id = await Own(owner, widgets, "manual", GitResourceMaterialization.Allocated);
        var source = sameSource ? owner : await Create();
        var result = await Services.GetRequiredService<GitAdoptionCandidates>()
            .ReadAsync(
                new ReadGitAdoptionCandidates(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    widgets.Kind,
                    new GitNamedCandidateSearch("manual"),
                    new DeclarationAdoptionCandidatePageRequest(1, 50)),
                default);
        var candidate = Assert.Single(Assert.IsType<AdoptionCandidatePage>(result.Value).Items);
        Assert.Equal(id, candidate.ResourceId);
        Assert.Equal(owner.Id, candidate.OwnerSourceId);
        Assert.Equal(sameSource, candidate.Selectable);
        Assert.Equal(sameSource ? null : "OwnershipConflict", candidate.Blocker);
        Assert.Equal(
            GitResourceMaterialization.Allocated,
            (await Inventory.Find(_ => true).SingleAsync()).Materialization);
    }

    [Fact]
    public async Task Candidate_read_supports_a_third_party_provider_without_engine_changes_or_writes()
    {
        var source = await Create();
        var id = ObjectId.GenerateNewId().ToString();
        await widgets.Documents.InsertOneAsync(
            Document(source.TenantId, id, "manual", "identity", "private-value", null, 1));
        var result = await Services.GetRequiredService<GitAdoptionCandidates>()
            .ReadAsync(
                new ReadGitAdoptionCandidates(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    widgets.Kind,
                    new GitNamedCandidateSearch("manual"),
                    new DeclarationAdoptionCandidatePageRequest(1, 50)),
                default);
        var candidate = Assert.Single(Assert.IsType<AdoptionCandidatePage>(result.Value).Items);
        Assert.Equal(widgets.Kind, candidate.Kind);
        Assert.Equal(id, candidate.ResourceId);
        Assert.True(candidate.Selectable);
        Assert.Equal(0, widgets.ApplyCalls);
        Assert.Equal(0, widgets.Deletions);
        Assert.Empty(await Inventory.Find(_ => true).ToListAsync());
    }
}
