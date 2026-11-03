using System.Net;
using Pole.Tests.Integration.Http;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Endpoints.Sources.Previews;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class RepositorySourceApiTests
{
    [Theory]
    [InlineData("Retain", GitPreviewRemovalDisposition.Retain, GitLifecycleAction.Retain)]
    [InlineData("Remove", GitPreviewRemovalDisposition.Remove, GitLifecycleAction.Remove)]
    [InlineData(null, GitPreviewRemovalDisposition.Remove, GitLifecycleAction.Remove)]
    public async Task Resource_changes_show_the_server_stored_removal_choice(
        string? removal, GitPreviewRemovalDisposition expected, GitLifecycleAction action)
    {
        var source = await Create(files: [GroupManifest("old")]);
        await Run(source.Id);
        repository.Publish("repo", [GroupManifest("next")]);
        object body = removal is null ? new { intent = "ResourceChanges", expectedRevision = source.Revision } :
            new { intent = "ResourceChanges", expectedRevision = source.Revision, removal };
        using var response = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        Assert.Equal(action, Assert.Single(view.Actions.Items, item => item.Resource.Name == "old").Action);
        Assert.Equal(GitLifecycleAction.Apply,
            Assert.Single(view.Actions.Items, item => item.Resource.Name == "next").Action);
        using var scope = ApiServices.CreateScope();
        var preview = (await scope.ServiceProvider.GetRequiredService<GitLifecyclePreviews>()
            .LoadAsync(new(TestData.TenantId, source.Id), view.Id, default))!;
        Assert.Equal(expected, Assert.IsType<GitPreviewResourceChanges>(preview.Intent).Removal);

        using var confirmed = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews/{view.Id}/confirm",
            new { expectedRevision = view.Revision });
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        await Run(source.Id);
        var current = (await Read(source.Id))!;
        Assert.Equal(RepositoryRunStatus.ConfigurationApplied, current.Status);
        var groups = scope.ServiceProvider.GetRequiredService<IMongoCollection<ResourceGroup>>();
        Assert.Equal(expected == GitPreviewRemovalDisposition.Retain ? 1 : 0,
            await groups.CountDocumentsAsync(item => item.TenantId == TestData.TenantId && item.Name == "old"));
        Assert.Equal(1, await groups.CountDocumentsAsync(item =>
            item.TenantId == TestData.TenantId && item.Name == "next"));
        var inventory = scope.ServiceProvider.GetRequiredService<IMongoCollection<ManagedDeclaration>>();
        if (expected == GitPreviewRemovalDisposition.Retain)
        {
            var retained = await inventory.Find(item => item.SourceId == source.Id && item.Name == "old").SingleAsync();
            Assert.True(retained.PendingRemoval);
        }
        else
            Assert.Equal(0, await inventory.CountDocumentsAsync(item =>
                item.SourceId == source.Id && item.Name == "old"));
    }

    [Fact]
    public async Task Retain_preview_requires_source_policy_change_and_invalidates_old_approval()
    {
        var source = await Create(files: [GroupManifest("old")]);
        await Run(source.Id);
        repository.Publish("repo", [GroupManifest("next")]);
        using var oldResponse = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews",
            new { intent = "ResourceChanges", expectedRevision = source.Revision, removal = "Retain" });
        var old = (await oldResponse.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        using var changed = await Client.PutAsJsonAsync($"{Root}/{source.Id}",
            new UpdateRepositorySourceBody(source.Revision, 300, GitPrunePolicy.Prune));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var pruned = (await changed.Content.ReadFromJsonAsync<RepositorySourceView>())!;
        var reads = repository.ReadCommits.Count;
        using var rejected = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews",
            new { intent = "ResourceChanges", expectedRevision = pruned.Revision, removal = "Retain" });
        await AssertProblem(rejected, HttpStatusCode.Conflict, "gitops.retain-policy-required");
        Assert.Equal(reads, repository.ReadCommits.Count);
        using var scope = ApiServices.CreateScope();
        var previews = scope.ServiceProvider.GetRequiredService<IMongoCollection<GitOperationPreview>>();
        Assert.Equal(1, await previews.CountDocumentsAsync(_ => true));

        using var restored = await Client.PutAsJsonAsync($"{Root}/{source.Id}",
            new UpdateRepositorySourceBody(pruned.Revision, 300, GitPrunePolicy.Retain));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var retained = (await restored.Content.ReadFromJsonAsync<RepositorySourceView>())!;
        using var stale = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews/{old.Id}/confirm",
            new { expectedRevision = old.Revision });
        await AssertProblem(stale, HttpStatusCode.Conflict, "gitops.preview-stale");
        using var accepted = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews",
            new { intent = "ResourceChanges", expectedRevision = retained.Revision, removal = "Retain" });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var view = (await accepted.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        Assert.Equal(GitLifecycleAction.Retain,
            Assert.Single(view.Actions.Items, item => item.Resource.Name == "old").Action);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("\"Unknown\"")]
    public async Task Resource_changes_reject_non_string_or_unknown_removal_choices(string removal)
    {
        var source = await Create();
        var json = "{\"intent\":\"ResourceChanges\",\"expectedRevision\":1,\"removal\":" + removal + "}";
        using var response = await Client.PostAsync($"{Root}/{source.Id}/previews",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Resource_changes_validator_rejects_unknown_typed_removal_choices()
    {
        var result = new CreateLifecyclePreviewValidator().Validate(new CreateLifecyclePreviewRequest(
            TestData.TenantId, new ResourceChangesPreviewBody(1, (GitPreviewRemovalDisposition)99), TestData.TenantId));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == "removal");
    }

    private static ManifestFile GroupManifest(string name) => new($"{name}.yaml", $$"""
        apiVersion: pole.io/v1alpha1
        kind: ResourceGroup
        metadata: {name: {{name}}}
        spec: {}
        """);
}
