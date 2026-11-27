// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class ManagedIdentityDriftTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("ResourceGroup")]
    [InlineData("Secret")]
    [InlineData("SecretBinding")]
    [InlineData("WebService")]
    public async Task RenamingAcceptedResourceBlocksInsteadOfSilentlyChangingItsAddress(string kind)
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var owned = await Inventory.Find(item => item.TenantId == Data.TenantId && item.Kind == kind).SingleAsync();
        var groups = Fixture.Database.GetCollection<ResourceGroup>("ResourceGroups");
        if (kind == "ResourceGroup")
        {
            await groups.UpdateOneAsync(
                item => item.Id == owned.ResourceId,
                Builders<ResourceGroup>.Update.Set(item => item.Name, "renamed"));
        }
        else if (kind == "Secret")
        {
            await Secrets.UpdateOneAsync(
                item => item.Id == owned.ResourceId,
                Builders<Secret>.Update.Set(item => item.Name, "renamed"));
        }
        else if (kind == "WebService")
        {
            await WebServices.UpdateOneAsync(
                item => item.Id == owned.ResourceId,
                Builders<WebService>.Update.Set(item => item.Name, "renamed"));
        }
        else
        {
            var secret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId))
                .SingleAsync();
            await Secrets.ReplaceOneAsync(
                item => item.Id == secret.Id,
                secret with
                {
                    Bindings = secret.Bindings
                        .Select(item => item with { DeclarationName = "renamed" })
                        .ToImmutableArray(),
                });
        }

        var events = await BusinessEvents();
        var result = await Apply();
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(events, await BusinessEvents());
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId && item.Kind == kind)
            .ToListAsync();
        Assert.Contains(receipts, item => item.Blocker == nameof(DeclarationIdentityChanged));
    }

    [Theory]
    [InlineData("Secret")]
    [InlineData("SecretBinding")]
    [InlineData("WebService")]
    public async Task PreviouslyCreatedProviderResourceIsNeverRecreatedWhenMissing(string kind)
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var owned = await Inventory.Find(item => item.TenantId == Data.TenantId && item.Kind == kind).SingleAsync();
        if (kind == "Secret")
        {
            await Secrets.DeleteOneAsync(item => item.Id == owned.ResourceId);
        }
        else if (kind == "WebService")
        {
            await WebServices.DeleteOneAsync(item => item.Id == owned.ResourceId);
        }
        else
        {
            var secret = await Secrets.Find(Builders<Secret>.Filter.Eq(item => item.TenantId, Data.TenantId))
                .SingleAsync();
            await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret with { Bindings = [] });
        }

        var events = await BusinessEvents();
        var result = await Apply();
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(events, await BusinessEvents());
        var receipts = await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == result.Context!.RunId && item.Kind == kind)
            .ToListAsync();
        Assert.Contains(receipts, item => item.Blocker == nameof(DeclarationManagedResourceMissing));
    }
}
