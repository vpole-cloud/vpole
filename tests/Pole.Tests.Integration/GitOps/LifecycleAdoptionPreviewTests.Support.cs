using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected async Task RetainAsManualAsync()
    {
        var sources = Services.GetRequiredService<GitRepositorySources>();
        var owner = (await sources.LoadAsync(Data.TenantId, source, default))!;
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>(
            (await previews.CreateAsync(
                new CreateLifecyclePreview(
                    new GitSourceIdentity(owner.TenantId, owner.Id),
                    owner.Revision,
                    new GitPreviewDetachSource()),
                default)).Value);
        Assert.IsType<GitLifecycleScheduled>(
            (await previews.ConfirmAsync(
                new ConfirmLifecyclePreview(
                    new GitSourceIdentity(owner.TenantId, owner.Id),
                    preview.Id,
                    preview.Revision,
                    Data.UserId),
                default)).Value);
        await Services.GetRequiredService<RepositorySourceJob>()
            .ProcessAsync(new GitSourceIdentity(owner.TenantId, owner.Id), default);
        Assert.Equal(
            GitSourceActivity.Detached,
            (await sources.LoadAsync(owner.TenantId, owner.Id, default))!.Activity);
        Assert.Equal(0, await Inventory.CountDocumentsAsync(item => item.SourceId == owner.Id));
    }

    private protected async Task<RepositorySource> AdoptionSourceAsync(ManifestFile[] files)
    {
        var repositoryId = Id();
        repository.Publish(repositoryId, files);
        return Assert.IsType<RepositorySource>(
            (await Services.GetRequiredService<GitRepositorySources>()
                .CreateAsync(
                    new CreateGitRepositorySource(
                        Data.TenantId,
                        Id(),
                        new GitRepositoryLocation(
                            repository.Provider,
                            TestRepositorySnapshots.ConnectionId,
                            repositoryId,
                            "main",
                            ""),
                        new GitSyncPolicy(TimeSpan.FromMinutes(5)),
                        new GitSourceAudit(Data.UserId, DateTime.UtcNow),
                        TestRepositorySnapshots.DisplayUrl),
                    default)).Value);
    }

    private protected ManifestFile AdoptionManifest(string kind, ManifestFile[] files)
    {
        var declaration = Services.GetRequiredService<ManifestCompiler>()
            .Compile(files)
            .Declarations
            .Single(item => item.Address.Kind == kind);
        return new ManifestFile(
            "adopt.yaml",
            JsonSerializer.Serialize(
                new
                {
                    apiVersion = ManifestCompiler.ApiVersion,
                    kind,
                    metadata = new { name = declaration.Address.Name },
                    spec = declaration.Spec,
                },
                DeclarationJson.Options));
    }
}
