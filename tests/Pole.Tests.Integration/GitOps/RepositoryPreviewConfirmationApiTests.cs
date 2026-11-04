using System.Net;
using Pole.Tests.Integration.Http;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application.Endpoints.Sources.Previews;
using Pole.Features.GitOps.Application.Lifecycle.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class RepositorySourceApiTests
{
    [Fact]
    public async Task Preview_confirmation_rejects_an_advanced_commit_as_stale_without_consuming_approval()
    {
        var source = await Create(files: [GroupManifest("keep")]);
        var preview = await CreateRepositoryPreview(source.Id, source.Revision);
        repository.Publish("repo", [GroupManifest("keep")], "advanced");

        using var response = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews/{preview.Id}/confirm",
            new { expectedRevision = preview.Revision });
        await AssertProblem(response, HttpStatusCode.Conflict, "gitops.preview-stale");
        await AssertPreviewAwaiting(source.Id, preview);
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("Missing")]
    [InlineData("Temporary")]
    public async Task Preview_confirmation_keeps_unavailable_access_distinct_from_stale_approval(string failure)
    {
        var source = await Create(files: [GroupManifest("keep")]);
        var preview = await CreateRepositoryPreview(source.Id, source.Revision);
        repository.Access = failure switch
        {
            "Denied" => new RepositoryAccessDenied(),
            "Missing" => new RepositoryNotFound(),
            "Temporary" => new RepositoryTemporarilyUnavailable(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        using var response = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews/{preview.Id}/confirm",
            new { expectedRevision = preview.Revision });
        await AssertProblem(response, HttpStatusCode.Conflict, "gitops.preview-input-unavailable");
        await AssertPreviewAwaiting(source.Id, preview);
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("Missing")]
    [InlineData("RefMissing")]
    [InlineData("Temporary")]
    public async Task Preview_confirmation_keeps_unavailable_resolution_distinct_from_stale_approval(string failure)
    {
        var source = await Create(files: [GroupManifest("keep")]);
        var preview = await CreateRepositoryPreview(source.Id, source.Revision);
        ResolveRepositoryCommitOutcome unavailable = failure switch
        {
            "Denied" => new RepositoryAccessDenied(),
            "Missing" => new RepositoryNotFound(),
            "RefMissing" => new RepositoryRefNotFound("main"),
            "Temporary" => new RepositoryTemporarilyUnavailable(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        repository.ResolveOverride = (_, _) => Task.FromResult(unavailable);

        using var response = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews/{preview.Id}/confirm",
            new { expectedRevision = preview.Revision });
        await AssertProblem(response, HttpStatusCode.Conflict, "gitops.preview-input-unavailable");
        await AssertPreviewAwaiting(source.Id, preview);
    }

    private async Task<LifecyclePreviewView> CreateRepositoryPreview(string sourceId, long revision)
    {
        using var response = await Client.PostAsJsonAsync($"{Root}/{sourceId}/previews",
            new { intent = "ResourceChanges", expectedRevision = revision });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
    }

    private async Task AssertPreviewAwaiting(string sourceId, LifecyclePreviewView preview)
    {
        using var scope = ApiServices.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<GitLifecyclePreviews>()
            .LoadAsync(new(TestData.TenantId, sourceId), preview.Id, default);
        Assert.NotNull(stored);
        Assert.IsType<GitPreviewAwaitingConfirmation>(stored.State);
        Assert.Equal(preview.Revision, stored.Revision);
        Assert.Null((await Read(sourceId))!.Synchronization.CurrentRunId);
    }
}
