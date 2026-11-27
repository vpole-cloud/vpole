// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Pole.Features.GitOps.Contracts;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class RepositoryPreviewConfirmationApiTests(
    MongoReplicaSetFixture fixture
) : RepositorySourceApiTestBase(fixture)
{
    [Fact]
    public async Task Preview_confirmation_rejects_an_advanced_commit_as_stale_without_consuming_approval()
    {
        var source = await Create(files: [GroupManifest("keep")]);
        var preview = await CreateRepositoryPreview(source.Id, source.Revision);
        repository.Publish("repo", [GroupManifest("keep")], "advanced");

        using var response = await Client.PostAsJsonAsync(
            $"{Root}/{source.Id}/previews/{preview.Id}/confirm",
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
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        using var response = await Client.PostAsJsonAsync(
            $"{Root}/{source.Id}/previews/{preview.Id}/confirm",
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
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        repository.ResolveOverride = (_, _) => Task.FromResult(unavailable);

        using var response = await Client.PostAsJsonAsync(
            $"{Root}/{source.Id}/previews/{preview.Id}/confirm",
            new { expectedRevision = preview.Revision });
        await AssertProblem(response, HttpStatusCode.Conflict, "gitops.preview-input-unavailable");
        await AssertPreviewAwaiting(source.Id, preview);
    }
}
