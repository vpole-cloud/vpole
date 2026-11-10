using System.Net;
using Pole.Features.GitOps.Application.Endpoints.Sources.Previews;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class RepositoryPreviewMetadataApiTests(
    MongoReplicaSetFixture fixture
) : RepositorySourceApiTestBase(fixture)
{
    [Fact]
    public async Task Preview_summary_covers_warnings_beyond_the_requested_action_page()
    {
        var source = await Create();
        await Run(source.Id);
        repository.Publish("repo", []);
        using var response = await Client.PostAsJsonAsync(
            $"{Root}/{source.Id}/previews",
            new { intent = "ResourceChanges", expectedRevision = source.Revision });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        var first = (await Client.GetFromJsonAsync<LifecyclePreviewView>(
            $"{Root}/{source.Id}/previews/{preview.Id}?pageSize=1"))!;
        Assert.Single(first.Actions.Items);
        Assert.True(first.Actions.HasMore);
        Assert.Equal(4, first.Actions.Total);
        Assert.Equal(4, first.Summary.Total);
        Assert.Equal(4, first.Summary.Remove);
        Assert.Equal(0, first.Summary.Apply);
        Assert.Contains("ProtectedData", first.Summary.Warnings);
        Assert.DoesNotContain("ProtectedData", Assert.Single(first.Actions.Items).Warnings);
        var input = Assert.IsType<LifecycleRepositoryPreviewInputView>(first.Input);
        Assert.Equal("repo", input.Repository.ExternalRepositoryId);
        Assert.NotEmpty(input.DesiredHash);
        var last = (await Client.GetFromJsonAsync<LifecyclePreviewView>(
            $"{Root}/{source.Id}/previews/{preview.Id}?page=4&pageSize=1"))!;
        Assert.False(last.Actions.HasMore);
        Assert.Equal(first.Summary.Warnings, last.Summary.Warnings);
        Assert.Equal(first.Summary.Total, last.Summary.Total);
        var json = await response.Content.ReadAsStringAsync();
        foreach (var field in new[] { "specJson", "keyMappings", "image", "dockerfile", "snapshot" })
            Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inventory_preview_has_typed_offline_input_and_zero_repository_fields()
    {
        var source = await Create(files: [GroupManifest("keep")]);
        await Run(source.Id);
        using var response = await Client.PostAsJsonAsync(
            $"{Root}/{source.Id}/previews",
            new { intent = "Detach", expectedRevision = source.Revision });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        Assert.IsType<LifecycleInventoryPreviewInputView>(preview.Input);
        Assert.Equal(1, preview.Summary.Detach);
        Assert.Empty(preview.Summary.Warnings);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"kind\":\"Inventory\"", json);
        Assert.DoesNotContain("\"repository\"", json);
        Assert.DoesNotContain("desiredHash", json);
    }

    [Fact]
    public async Task Relocation_preview_reads_expose_its_stored_destination_after_reopening()
    {
        var source = await Create(files: [GroupManifest("keep")]);
        await Run(source.Id);
        repository.Publish("destination-repository", [GroupManifest("keep")]);
        using var response = await Client.PostAsJsonAsync(
            $"{Root}/{source.Id}/previews",
            new
            {
                intent = "Relocation",
                expectedRevision = source.Revision,
                destination = new
                {
                    provider = "TestRepository",
                    connectionId = TestRepositorySnapshots.ConnectionId,
                    externalRepositoryId = "destination-repository",
                    branch = "release/next",
                    manifestDirectory = "apps",
                },
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        var reopened
            = (await Client.GetFromJsonAsync<LifecyclePreviewView>($"{Root}/{source.Id}/previews/{created.Id}"))!;
        var input = Assert.IsType<LifecycleRepositoryPreviewInputView>(reopened.Input);
        Assert.Equal("destination-repository", input.Repository.ExternalRepositoryId);
        Assert.Equal("release/next", input.Repository.Branch);
        Assert.Equal("apps", input.Repository.ManifestDirectory);
        Assert.Equal("repo", (await Read(source.Id))!.Repository.ExternalRepositoryId);
    }
}
