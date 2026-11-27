// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class GitLifecyclePreviewProjectionTests
{
    [Theory]
    [InlineData("PostgresCluster")]
    [InlineData("RedisService")]
    [InlineData("Secret")]
    public async Task Protected_replacement_warns_about_identity_downtime_and_data_loss(string kind)
    {
        var source = await Create();
        var provider = Services.GetServices<IDeclarativeResourceProvider>()
            .Cast<ProjectionProvider>()
            .Single(item => item.Kind == kind);
        var original = await Own(source, provider, "protected");
        repository.Files = [Manifest(kind, "protected", "new-identity")];
        var preview = Assert.IsType<GitOperationPreview>(
            (await Previews.CreateAsync(
                new CreateLifecyclePreview(
                    new GitSourceIdentity(source.TenantId, source.Id),
                    source.Revision,
                    new GitPreviewReplacement(
                    [
                        new GitReplacementSelection(
                            new GitResourceIdentity(kind, "protected", original),
                            kind,
                            "protected"),
                    ])),
                default)).Value);
        var action = Assert.Single(preview.Operations);
        Assert.Equal(GitLifecycleAction.Replace, action.Action);
        Assert.Equal(GitLifecycleProtection.ProtectedData, action.Protection);
        Assert.Equal(
            new[]
            {
                "DataMayBeLost", "Downtime", "IdentityWillChange", "NoAutomaticDataMigration",
                "ProtectedData",
            },
            action.Warnings);
        Assert.Equal(action.Warnings.Length, action.Warnings.Distinct().Count());
    }
}
