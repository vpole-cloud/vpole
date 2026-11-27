// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application.Endpoints.Sources.Previews;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Http;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class RepositorySourceApiTestBase
{
    private protected async Task<LifecyclePreviewView> CreateRepositoryPreview(string sourceId, long revision)
    {
        using var response = await Client.PostAsJsonAsync(
            $"{Root}/{sourceId}/previews",
            new { intent = "ResourceChanges", expectedRevision = revision });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
    }

    private protected async Task AssertPreviewAwaiting(string sourceId, LifecyclePreviewView preview)
    {
        using var scope = ApiServices.CreateScope();
        var stored = await scope.ServiceProvider
            .GetRequiredService<GitLifecyclePreviews>()
            .LoadAsync(new GitSourceIdentity(Data.TenantId, sourceId), preview.Id, default);
        Assert.NotNull(stored);
        Assert.IsType<GitPreviewAwaitingConfirmation>(stored.State);
        Assert.Equal(preview.Revision, stored.Revision);
        Assert.Null((await Read(sourceId))!.Synchronization.CurrentRunId);
    }
}
