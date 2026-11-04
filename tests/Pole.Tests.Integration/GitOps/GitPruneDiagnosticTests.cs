using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitPrunePlanningTests
{
    [Fact]
    public async Task Manual_dependency_rejection_keeps_its_typed_diagnostic_in_persisted_and_API_run_results()
    {
        var source = await Create();
        await Own(source, widgets, "old", "old-id");
        await Insert(widgets, source.TenantId, "manual", "manual-id");
        await widgets.Documents.UpdateOneAsync(item => item["_id"] == "manual-id",
            Builders<BsonDocument>.Update.Set("TargetResourceId", "old-id"));
        repository.Files = [];
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var run = await Latest(source);
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Equal(nameof(GitLifecycleDependencyRetained), Assert.Single(run.Diagnostics).Code);
        var view = (await Services.GetRequiredService<GitRepositoryQueries>().RunAsync(source.TenantId,
            source.Id, run.Id, default))!;
        Assert.Equal(nameof(GitLifecycleDependencyRetained), Assert.Single(view.Diagnostics).Code);
        Assert.Equal(2, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal(0, widgets.ApplyCalls);
    }

    [Fact]
    public async Task Unknown_owner_preview_failure_remains_generic_without_leaking_owner_payload()
    {
        var source = await Create();
        await Own(source, widgets, "old", "old-id");
        const string payload = "owner-private-diagnostic-payload";
        widgets.LifecycleFailure = new OwnerPreviewFailure(payload);
        repository.Files = [];
        await Worker.ProcessAsync(new(source.TenantId, source.Id), default);

        var run = await Latest(source);
        Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.Equal("LifecyclePreviewBlocked", Assert.Single(run.Diagnostics).Code);
        var view = (await Services.GetRequiredService<GitRepositoryQueries>().RunAsync(source.TenantId,
            source.Id, run.Id, default))!;
        Assert.Equal("LifecyclePreviewBlocked", Assert.Single(view.Diagnostics).Code);
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(payload, json);
        Assert.DoesNotContain(nameof(OwnerPreviewFailure), json);
        Assert.Equal(1, await widgets.Documents.CountDocumentsAsync(_ => true));
        Assert.Equal(0, widgets.ApplyCalls);
    }

    private sealed record OwnerPreviewFailure(string Payload) : IDeclarationFailure;
}
