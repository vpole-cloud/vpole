using Pole.Features.GitOps.Application.Lifecycle.Mongo;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Migrations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Migrations;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class SecretAdoptionExecutionTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private readonly TestRepositorySnapshots repository = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new GitOpsIndexesMigration().Up(Fixture.Database);
        await new SecretIndexesMigration().Up(Fixture.Database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_secret_adoption_pins_scope_versions_and_preserves_existing_values(bool scopeChanged)
    {
        const string syntheticValue = "synthetic-adoption-fixture";
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("manual-group"));
        var secret = Assert.IsType<Secret>((await Services.GetRequiredService<PersistCreatedSecret>()(
            group.TenantId, group.Id, group.DefaultIsolationScopeId, "manual-secret", "test purpose",
            new() { ["token"] = syntheticValue }, default)).Value);
        var secrets = Fixture.Database.GetCollection<Secret>("Secrets");
        secret = await secrets.Find(item => item.Id == secret.Id).SingleAsync();
        var versions = Fixture.Database.GetCollection<SecretVersion>("SecretVersions");
        var encrypted = Convert.ToHexStringLower(SHA256.HashData(
            (await versions.Find(item => item.SecretId == secret.Id).SingleAsync()).ToBson()));
        var repositoryId = ObjectId.GenerateNewId().ToString();
        repository.Publish(repositoryId,
        [
            new("secret.yaml", JsonSerializer.Serialize(new
            {
                apiVersion = "pole.io/v1alpha1",
                kind = "Secret",
                metadata = new { name = secret.Name },
                spec = new { resourceGroup = group.Name, purpose = secret.Purpose, keys = secret.KeyNames }
            }, DeclarationJson.Options))
        ]);
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var source = Assert.IsType<RepositorySource>((await sources.CreateAsync(new(TestData.TenantId,
            Guid.NewGuid().ToString("D"),
            new(repository.Provider, TestRepositorySnapshots.ConnectionId, repositoryId, "main", ""),
            new(TimeSpan.FromMinutes(5)), new(TestData.UserId, DateTime.UtcNow),
            TestRepositorySnapshots.DisplayUrl), default)).Value);
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(
            new(source.TenantId, source.Id), source.Revision,
            new GitPreviewAdoption([new("Secret", secret.Name, secret.Id)])), default)).Value);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(source.TenantId, source.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);
        if (scopeChanged)
            await DbContext.IsolationScopes.UpdateOneAsync(item => item.Id == group.DefaultIsolationScopeId,
                Builders<IsolationScope>.Update.Inc(item => item.Revision, 1));

        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(source.TenantId, source.Id), default);
        var run = (await sources.RunAsync(source.TenantId, source.Id, scheduled.RunId, default))!;
        var completion = Assert.IsType<GitRunFinished>(run.State).Completion;
        var inventory = Services.GetRequiredService<IMongoCollection<ManagedDeclaration>>();
        var receipts = Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>();
        if (scopeChanged)
        {
            Assert.Equal(GitRunCompletion.Blocked, completion);
            Assert.Contains(run.Diagnostics, item => item.Code == nameof(GitLifecycleApprovalStale));
            Assert.False(await inventory.Find(item => item.SourceId == source.Id).AnyAsync());
            Assert.False(await receipts.Find(item => item.RunId == run.Id).AnyAsync());
        }
        else
        {
            Assert.True(completion == GitRunCompletion.ConfigurationApplied,
                string.Join(",", run.Diagnostics.Select(item => item.Code)));
            var expected = Assert.Single(preview.Operations).Expected!;
            Assert.Contains(expected.Versions, item => item.DocumentId == $"ResourceGroup/{group.Id}");
            Assert.Contains(expected.Versions, item => item.DocumentId ==
                $"IsolationScope/{group.DefaultIsolationScopeId}");
            Assert.Equal(secret.Id,
                (await inventory.Find(item => item.SourceId == source.Id).SingleAsync()).ResourceId);
            Assert.Single(await receipts.Find(item => item.RunId == run.Id && item.Lifecycle!.Step ==
                GitLifecycleStep.Adopt).ToListAsync());
        }

        var current = await secrets.Find(item => item.Id == secret.Id).SingleAsync();
        Assert.Equal(secret.CurrentVersion, current.CurrentVersion);
        Assert.Equal(secret.Revision, current.Revision);
        Assert.Equal(secret.UpdatedAtUtc, current.UpdatedAtUtc);
        Assert.True(current.HasValue("token"));
        Assert.Equal(encrypted, Convert.ToHexStringLower(SHA256.HashData(
            (await versions.Find(item => item.SecretId == secret.Id).SingleAsync()).ToBson())));
        Assert.DoesNotContain(syntheticValue, JsonSerializer.Serialize(preview));
        Assert.DoesNotContain(syntheticValue, JsonSerializer.Serialize(run));
    }
}
