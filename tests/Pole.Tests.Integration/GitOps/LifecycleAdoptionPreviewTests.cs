using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using Pole.Features.Secrets.Domain;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task WebService_adoption_preview_rejects_immutable_group_change_before_confirmation()
    {
        var originalFiles = StageFiveWebSnapshot(_ => { });
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(originalFiles)).Status);
        var service = await Service();
        await RetainAsManualAsync();
        await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup() with { Name = "other" });
        var changedFiles = StageFiveWebSnapshot(spec => spec["resourceGroup"] = "other");
        var file = AdoptionManifest("WebService", changedFiles);
        var adopter = await AdoptionSourceAsync([file]);
        var before = await BusinessEvents();
        var outcome = await Services.GetRequiredService<GitLifecyclePreviews>().CreateAsync(
            new(new(adopter.TenantId, adopter.Id), adopter.Revision,
                new GitPreviewAdoption([new("WebService", service.Name, service.Id)])), default);
        var rejected = Assert.IsType<GitPreviewRejected>(outcome.Value);
        Assert.IsType<DeclarationReplacementRequired>(rejected.Failure);
        Assert.Equal(service.ResourceGroupId, (await Service()).ResourceGroupId);
        Assert.Equal(service.Revision, (await Service()).Revision);
        Assert.Equal(before, await BusinessEvents());
        Assert.Equal(0, await Inventory.CountDocumentsAsync(item => item.SourceId == adopter.Id));
        Assert.Equal(0, await Services.GetRequiredService<IMongoCollection<GitOperationPreview>>()
            .CountDocumentsAsync(item => item.Source.Source.SourceId == adopter.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unnamed_manual_binding_adoption_uses_fresh_own_reads_and_rejects_external_changes(bool external)
    {
        await Apply();
        await Supply();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply()).Status);
        await RetainAsManualAsync();
        var secret = await Secrets.Find(_ => true).SingleAsync();
        var binding = Assert.Single(secret.Bindings);
        var unnamed = secret with
        {
            Bindings = [binding with { DeclarationName = null }],
            Revision = secret.Revision + 1
        };
        await Secrets.ReplaceOneAsync(item => item.Id == secret.Id, unnamed);
        var versions = Services.GetRequiredService<IMongoCollection<SecretVersion>>();
        var encrypted = await versions.Find(item => item.SecretId == secret.Id).SingleAsync();
        var valueHash = Convert.ToHexStringLower(SHA256.HashData(encrypted.ToBson()));
        var document = JsonNode.Parse(AdoptionManifest("SecretBinding", Snapshot()).Content)!;
        document["metadata"]!["name"] = "adopted-binding";
        var adopter = await AdoptionSourceAsync([new("binding.yaml", document.ToJsonString())]);
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(
            new(new(adopter.TenantId, adopter.Id), adopter.Revision,
                new GitPreviewAdoption([new("SecretBinding", "adopted-binding", binding.Id)])), default)).Value);
        Assert.Null(Assert.Single((await Secrets.Find(_ => true).SingleAsync()).Bindings).DeclarationName);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(
            new(new(adopter.TenantId, adopter.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        if (external)
            await Secrets.UpdateOneAsync(item => item.Id == secret.Id,
                Builders<Secret>.Update.Inc(item => item.ReferenceFence, 1));
        await Services.GetRequiredService<RepositorySourceJob>()
            .ProcessAsync(new(adopter.TenantId, adopter.Id), default);
        var run = (await Services.GetRequiredService<GitRepositorySources>().RunAsync(adopter.TenantId,
            adopter.Id, scheduled.RunId, default))!;
        if (external)
        {
            Assert.Equal(GitRunCompletion.Blocked, Assert.IsType<GitRunFinished>(run.State).Completion);
            Assert.Equal(nameof(GitLifecycleApprovalStale), Assert.Single(run.Diagnostics).Code);
            Assert.Null(Assert.Single((await Secrets.Find(_ => true).SingleAsync()).Bindings).DeclarationName);
            Assert.Equal(0, await Inventory.CountDocumentsAsync(item => item.SourceId == adopter.Id));
            return;
        }
        Assert.Equal(GitRunCompletion.ConfigurationApplied, Assert.IsType<GitRunFinished>(run.State).Completion);
        var adopted = await Secrets.Find(_ => true).SingleAsync();
        var named = Assert.Single(adopted.Bindings);
        Assert.Equal(binding.Id, named.Id);
        Assert.Equal("adopted-binding", named.DeclarationName);
        Assert.Equal(binding.Consumer, named.Consumer);
        Assert.Equal(secret.CurrentVersion, adopted.CurrentVersion);
        Assert.Equal(valueHash, Convert.ToHexStringLower(SHA256.HashData(
            (await versions.Find(item => item.SecretId == secret.Id).SingleAsync()).ToBson())));
        var owned = await Inventory.Find(item => item.SourceId == adopter.Id).SingleAsync();
        Assert.Equal(binding.Id, owned.ResourceId);
        Assert.Equal("SecretBinding", owned.Kind);
    }

    private async Task RetainAsManualAsync()
    {
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var owner = (await sources.LoadAsync(TestData.TenantId, source, default))!;
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(
            new(new(owner.TenantId, owner.Id), owner.Revision, new GitPreviewDetachSource()), default)).Value);
        Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(
            new(new(owner.TenantId, owner.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        Assert.Equal(GitSourceActivity.Detached,
            (await sources.LoadAsync(owner.TenantId, owner.Id, default))!.Activity);
        Assert.Equal(0, await Inventory.CountDocumentsAsync(item => item.SourceId == owner.Id));
    }

    private async Task<RepositorySource> AdoptionSourceAsync(ManifestFile[] files)
    {
        var repositoryId = Id();
        repository.Publish(repositoryId, files);
        return Assert.IsType<RepositorySource>((await Services.GetRequiredService<GitRepositorySources>().CreateAsync(
            new(TestData.TenantId, Id(), new(repository.Provider, TestRepositorySnapshots.ConnectionId,
                    repositoryId, "main", ""), new(TimeSpan.FromMinutes(5)),
                new(TestData.UserId, DateTime.UtcNow), TestRepositorySnapshots.DisplayUrl), default)).Value);
    }

    private ManifestFile AdoptionManifest(string kind, ManifestFile[] files)
    {
        var declaration = Services.GetRequiredService<ManifestCompiler>().Compile(files).Declarations
            .Single(item => item.Address.Kind == kind);
        return new("adopt.yaml", JsonSerializer.Serialize(new
        {
            apiVersion = ManifestCompiler.ApiVersion,
            kind,
            metadata = new { name = declaration.Address.Name },
            spec = declaration.Spec
        }, DeclarationJson.Options));
    }
}
