using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task Redis_adoption_refreshes_receipt_versions_for_external_group_and_scope_without_rebinding()
    {
        await SeedRedisPlanningCatalog();
        var files = RedisSnapshot();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        await RetainAsManualAsync();
        using var host = TestServiceProviderFactory.Create(Fixture, services =>
        {
            ConfigureServices(services);
            NamedCatalogTestData.ConfigureIsolatedApi(services);
        });
        using var scope = host.CreateScope();
        var executionServices = scope.ServiceProvider;
        var original = await RedisResources.Find(_ => true).SingleAsync();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == original.Id).SingleAsync();
        var adopter = await AdoptionSourceAsync([AdoptionManifest("RedisService", files)]);
        var previews = executionServices.GetRequiredService<GitLifecyclePreviews>();
        var preview = Assert.IsType<GitOperationPreview>((await previews.CreateAsync(new(
            new(adopter.TenantId, adopter.Id), adopter.Revision,
            new GitPreviewAdoption([new("RedisService", original.Name, original.Id)])), default)).Value);
        Assert.Contains(preview.References, item => item.Target.Kind == "ResourceGroup" &&
            item.Target.ResourceId == original.ResourceGroupId);
        Assert.Contains(preview.References, item => item.Target.Kind == "IsolationScope" &&
            item.Target.ResourceId == placement.IsolationScopeId);
        var scheduled = Assert.IsType<GitLifecycleScheduled>((await previews.ConfirmAsync(new(
            new(adopter.TenantId, adopter.Id), preview.Id, preview.Revision, TestData.UserId), default)).Value);

        var claim = Assert.IsType<GitWorkClaim>((await executionServices.GetRequiredService<GitRunStore>()
            .ClaimAsync(new(adopter.TenantId, adopter.Id), "adoption-regression", default)).Value);
        var input = claim.Run.AcceptedSnapshot!;
        var compiler = executionServices.GetRequiredService<ManifestCompiler>();
        var declaration = Assert.Single(compiler.Restore(input.ApiVersion,
            input.Resources.Select(item => new AcceptedManifestDeclaration(item.Kind, item.Name, item.SpecJson))
                .ToArray()).Declarations);
        var descriptor = input.Operations.First();
        var context = new GitMutationContext(adopter.TenantId, adopter.Id, claim.Run.Id, claim.Run.Stamp.Fence,
            adopter.DisplayUrl, new(claim.Run.Stamp.ConfigurationRevision, claim.Run.Stamp.Generation,
                claim.Handle.Identity.WorkerId, claim.Handle.Identity.Attempt, input.Commit));
        var operation = new GitOperationInput(context, declaration, descriptor.Phase, original.Id);
        using (var session = await Fixture.Client.StartSessionAsync())
        {
            session.StartTransaction();
            var provider = Assert.IsAssignableFrom<ITransactionalDeclarativeResourceProvider>(
                executionServices.GetRequiredService<DeclarativeProviderRegistry>().Find("RedisService"));
            var expected = Assert.Single(preview.Operations).Expected!;
            var desired = Assert.IsType<DeclarationProjection>((await provider.ProjectDesiredAsync(new(
                adopter.TenantId, declaration, operation), session, default)).Value);
            Assert.All(desired.Dependencies!.Documents, version => Assert.Contains(expected.Versions,
                item => item.DocumentId == version.DocumentId && item.Revision == version.Revision));
            Assert.IsType<DeclarationFenced>((await provider.FenceAsync(new(operation,
                new(expected.Versions.Select(item => new DeclarationDocumentVersion(item.DocumentId, item.Revision))
                    .ToArray())), session, default)).Value);
            await session.AbortTransactionAsync();
        }

        await executionServices.GetRequiredService<GitLifecycleExecutor>().ExecuteAsync(claim, default);
        var run = (await executionServices.GetRequiredService<GitRepositorySources>()
            .RunAsync(adopter.TenantId, adopter.Id, scheduled.RunId, default))!;
        Assert.True(run.State is GitRunFinished { Completion: GitRunCompletion.ConfigurationApplied },
            string.Join(",", run.Diagnostics.Select(item => item.Code)));
        var current = await RedisResources.Find(item => item.Id == original.Id).SingleAsync();
        Assert.Equal(original.Revision, current.Revision);
        Assert.Equal(original.Spec, current.Spec);
        Assert.Equal(original.ResourceGroupId, current.ResourceGroupId);
        Assert.Equal(placement.IsolationScopeId, (await DbContext.ResourceDeployments
            .Find(item => item.Id == placement.Id).SingleAsync()).IsolationScopeId);
        Assert.Equal(placement.DesiredGeneration, (await DbContext.ResourceDeployments
            .Find(item => item.Id == placement.Id).SingleAsync()).DesiredGeneration);
        Assert.Equal(original.Id, (await Inventory.Find(item => item.SourceId == adopter.Id).SingleAsync()).ResourceId);
        var receipts = await executionServices.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .Find(item => item.RunId == run.Id).ToListAsync();
        var adopted = Assert.Single(receipts, item => item.Lifecycle?.Step == GitLifecycleStep.Adopt);
        Assert.Contains(receipts, item => item.Lifecycle?.Step == GitLifecycleStep.Apply);
        Assert.Equal(adopted.Lifecycle!.RelatedVersions.Length,
            adopted.Lifecycle.RelatedVersions.Select(item => item.DocumentId).Distinct().Count());
        var group = await DbContext.ResourceGroups.Find(item => item.Id == original.ResourceGroupId).SingleAsync();
        var finalReceipt = receipts.OrderBy(item => item.Lifecycle!.Sequence).Last();
        Assert.Equal(group.RegistrationFence, Assert.Single(finalReceipt.Lifecycle!.RelatedVersions,
            item => item.DocumentId == $"ResourceGroupFence/{group.Id}").Revision);
        Assert.Equal(preview.References.Select(item => item.Target),
            Assert.IsType<GitInputLifecycle>(run.Input).Plan.References.Select(item => item.Target));
        var replay = await executionServices.GetRequiredService<RepositorySourceJob>()
            .ProcessAsync(new(adopter.TenantId, adopter.Id), default);
        Assert.Equal(RepositoryExecutionDisposition.NoWork, replay.Disposition);
        Assert.Equal(receipts.Count, await executionServices.GetRequiredService<IMongoCollection<GitOperationReceipt>>()
            .CountDocumentsAsync(item => item.RunId == run.Id));
    }
}
