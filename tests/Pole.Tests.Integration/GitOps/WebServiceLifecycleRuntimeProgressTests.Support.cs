using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Domains.Domain;
using Pole.Features.Domains.Migrations;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected async Task<GitReconciliationRun> RemoveHostnameThenAdvanceRuntime(string progress, string? change)
    {
        await EnablePublicWebTargetAsync();
        ManifestFile[] files = [.. StageFiveWebSnapshot(spec => spec["exposure"] = "Public"), .. HostnameSnapshot()];
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var owner = (await Services.GetRequiredService<GitRepositorySources>()
            .LoadAsync(Data.TenantId, source, default))!;
        var service = await Service();
        foreach (var cell in await DbContext.RuntimeCells.Find(item => item.TenantId == Data.TenantId).ToListAsync())
            await DbContext.RuntimeCells.ReplaceOneAsync(item => item.Id == cell.Id,
                cell.MarkReady(cell.DesiredGeneration, DateTime.UtcNow));
        var binding = await DomainBindings.Find(
            ScenarioFilter<HostnameBinding>(item => item.TenantId) &
            Builders<HostnameBinding>.Filter.Eq("Identity._t", nameof(NamedDomainIdentity))).SingleAsync();
        repository.Publish(owner.Repository.ExternalRepositoryId,
            [AdoptionManifest("ResourceGroup", files), .. DomainZoneSnapshot()], "remove-workload");
        var previews = Services.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(new(owner.TenantId, owner.Id),
            owner.Revision, new GitPreviewResourceChanges(GitPreviewRemovalDisposition.Remove)), default)).Value);
        var web = Assert.Single(preview.Operations, item => item.Resource.Kind == "WebService");
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var provider = Services.GetServices<IDeclarativeResourceLifecycleProvider>()
                .Single(item => item.Kind == "WebService");
            var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>((await provider.ReadLifecycleAsync(new(
                owner.TenantId, new(new("WebService", service.Name), service.Id)), session, default)).Value);
            Assert.Equal(GitLifecycleVersionSemantics.ManagedConfiguration, snapshot.VersionSemantics);
            var reader = Services.GetRequiredService<GitLifecyclePreviewReferences>();
            var placement = await DbContext.ResourceDeployments.Find(session, item => item.ResourceId == service.Id)
                .SingleAsync();
            foreach (var action in new[] { GitLifecycleAction.Remove, GitLifecycleAction.Replace,
                         GitLifecycleAction.Detach, GitLifecycleAction.Retain })
            {
                var observed = (await reader.PreconditionsAsync(new(preview.Source, web with { Action = action },
                    preview.References), session, default))!;
                Assert.DoesNotContain(observed.Versions, item => item.DocumentId ==
                    $"ResourceDeployment/{placement.Id}");
                Assert.Contains(observed.Versions, item => item.DocumentId ==
                    $"ResourceDeployment/{placement.Id}/DesiredGeneration" &&
                    item.Revision == placement.DesiredGeneration);
            }
            var strict = (await reader.PreconditionsAsync(new(preview.Source,
                web with { Action = GitLifecycleAction.Apply }, preview.References), session, default))!;
            Assert.Contains(strict.Versions, item => item.DocumentId == $"ResourceDeployment/{placement.Id}" &&
                item.Revision == placement.Revision);
            await session.CommitTransactionAsync();
        }
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(owner.TenantId, owner.Id), preview.Id, preview.Revision, Data.UserId), default)).Value);
        var advanced = false;
        repository.BeforeAccess = async () =>
        {
            if (advanced || !await Services.GetRequiredService<IMongoCollection<GitOperationReceipt>>().Find(item =>
                    item.RunId == scheduled.RunId && item.Kind == "HostnameBinding" && item.Blocker == null).AnyAsync())
                return;
            advanced = true;
            var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id)
                .SingleAsync();
            var at = placement.UpdatedAtUtc;
            var claim = Id();
            var started = placement.Start(placement.DesiredGeneration, ResourceDeploymentOperation.Apply,
                claim, at, at.AddMinutes(1));
            var observed = progress == "fail"
                ? started.Fail(started.DesiredGeneration, ResourceDeploymentOperation.Apply, claim,
                    "FixtureApplyFailed", "Fixture runtime failure", false, at, at.AddMinutes(2))
                : started.Complete(started.DesiredGeneration, ResourceDeploymentOperation.Apply, claim, [], at);
            Assert.Equal(placement.DesiredGeneration, observed.DesiredGeneration);
            Assert.Equal(placement.ResourceRevision, observed.ResourceRevision);
            Assert.Equal(placement.ReferenceFence, observed.ReferenceFence);
            Assert.Equal(placement.Revision + 2, observed.Revision);
            if (progress == "observation-fence")
                observed = observed with { ReferenceFence = observed.ReferenceFence + 1 };
            await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, observed);
            await ChangeReviewedWebServiceAsync(change, owner, service, binding, observed);
        };
        await Services.GetRequiredService<RepositorySourceJob>().ProcessAsync(new(owner.TenantId, owner.Id), default);
        Assert.True(advanced);
        repository.BeforeAccess = null;
        return (await Services.GetRequiredService<GitRepositorySources>()
            .RunAsync(owner.TenantId, owner.Id, scheduled.RunId, default))!;
    }

    private protected async Task ChangeReviewedWebServiceAsync(string? change, RepositorySource owner, WebService service,
        HostnameBinding binding, ResourceDeployment placement)
    {
        switch (change)
        {
            case "root-configuration":
                await WebServices.UpdateOneAsync(item => item.Id == service.Id,
                    Builders<WebService>.Update.Set(item => item.Tags, ImmutableHashSet.Create("external")));
                break;
            case "root-revision":
                await WebServices.UpdateOneAsync(item => item.Id == service.Id,
                    Builders<WebService>.Update.Inc(item => item.Revision, 1));
                break;
            case "placement-configuration":
                await DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == placement.Id,
                    Builders<ResourceDeployment>.Update.Set(item =>
                        item.ResolvedPlacement.Compute.Limits.CpuMillis, 9999));
                break;
            case "generation":
                await DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == placement.Id,
                    Builders<ResourceDeployment>.Update.Inc(item => item.DesiredGeneration, 1));
                break;
            case "resource-revision":
                await DbContext.ResourceDeployments.UpdateOneAsync(item => item.Id == placement.Id,
                    Builders<ResourceDeployment>.Update.Inc(item => item.ResourceRevision, 1));
                break;
            case "reference":
                await DomainBindings.InsertOneAsync(binding with
                {
                    Id = Id(),
                    Identity = new NamedDomainIdentity("manual-alias"),
                    RelativeName = "manual",
                    Hostname = "manual.company." + Data.Hostname("apps")
                });
                break;
            case "ownership":
                await Inventory.UpdateOneAsync(item => item.ResourceId == service.Id,
                    Builders<ManagedDeclaration>.Update.Set(item => item.SourceId, Id()));
                break;
            case "source":
                await Services.GetRequiredService<GitRepositorySources>().ChangePolicyAsync(new(owner.TenantId,
                    owner.Id, owner.Revision, new(TimeSpan.FromMinutes(6)), owner.Prune), default);
                break;
        }
    }
}
