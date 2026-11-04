using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.WebServices.Application.Common;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Secrets.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task WebServiceSameCommitRepairsFullManagedConfigurationAndKeepsServerCredentials()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var service = await Service();
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        var connectionResult = await Services.GetRequiredService<PersistResourceConnectionCreation>()(
            new(service.TenantId, "self-http", WebServiceResourceDeployment.ResourceType, service.Id,
                WebServiceResourceDeployment.ResourceType, service.Id, "http"));
        var connection = Assert.IsType<ResourceConnection>(connectionResult.Value);
        var connectionReferences = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty
            .Add("SELF_HOST", new(connection.Id, "Host", null));
        await WebServices.ReplaceOneAsync(item => item.Id == service.Id, service with
        {
            Revision = service.Revision + 1,
            Spec = service.Spec with
            {
                Image = Assert.IsType<Pole.Features.WebServices.Domain.ImageInfo>(service.Spec.Image) with
                { Tag = "manual", SecretName = "server-pull-secret" },
                Config = service.Spec.Config with { ConnectionEnvironmentReferences = connectionReferences }
            }
        });
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, placement with
        {
            Revision = placement.Revision + 1,
            ResolvedPlacement = placement.ResolvedPlacement with
            {
                Compute = placement.ResolvedPlacement.Compute with
                {
                    Limits = placement.ResolvedPlacement.Compute.Limits with
                    { CpuMillis = placement.ResolvedPlacement.Compute.Limits.CpuMillis + 1 }
                }
            }
        });
        var releases = await Releases();
        var result = await Apply();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        var repaired = await Service();
        Assert.Equal(service.Spec.Image.ResolvedImage!.Tag, repaired.Spec.Image.ResolvedImage!.Tag);
        Assert.Equal("server-pull-secret", repaired.Spec.Image.ResolvedImage!.SecretName);
        Assert.Equal(connectionReferences.OrderBy(item => item.Key),
            repaired.Spec.Config.ConnectionEnvironmentReferences.OrderBy(item => item.Key));
        Assert.Equal(releases + 1, await Releases());
        var physical = await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync();
        Assert.Equal(placement.ResolvedPlacement.Compute.Limits, physical.ResolvedPlacement.Compute.Limits);
        var queries = Services.GetRequiredService<GitRepositoryQueries>();
        var operations = (await queries.OperationsAsync(TestData.TenantId, source, result.Context!.RunId, 1, 20,
            default))!.Items.Where(item => item.Kind == "WebService").ToArray();
        Assert.Equal(GitPlannedAction.NoOp,
            operations.Single(item => item.Phase == DeclarationPhase.Prepare).Plan!.Action);
        var configure = operations.Single(item => item.Phase == DeclarationPhase.Configure).Plan!;
        Assert.Equal(GitPlannedAction.Update, configure.Action);
        Assert.Contains("configuration.image.tag", configure.ChangedFields);
        Assert.Contains("configuration.compute.resolution.limits.cpuMillis", configure.ChangedFields);

        await Supply("second-rotation-private");
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == physical.Id, physical with
        {
            ObservedGeneration = physical.DesiredGeneration,
            State = new AppliedResourceDeploymentState(physical.DesiredGeneration, DateTime.UtcNow)
        });
        var events = await BusinessEvents();
        var stable = await Apply();
        Assert.Equal(repaired.Revision, (await Service()).Revision);
        Assert.Equal(releases + 1, await Releases());
        Assert.Equal(events, await BusinessEvents());
        var view = await queries.RunAsync(TestData.TenantId, source, stable.Context!.RunId, default);
        Assert.Equal(RepositoryRunStatus.AlreadyCurrent, view!.Status);
        Assert.Equal(new RepositoryOperationSummary(0, 0, 5, 0, 0, null), view.Operations);
    }

    [Fact]
    public async Task NoOpReplansWhenSecretReferenceFenceChangesBeforeApplyWithoutCreatingRelease()
    {
        var initial = await Apply();
        await Supply();
        await Pipeline.ResumeAsync(initial.Context!);
        var service = await Service();
        var secret = await Secrets.Find(Builders<Secret>.Filter.Empty).SingleAsync();
        var events = await BusinessEvents();
        var releases = await Releases();
        var intercepted = false;
        var mutations = new BeforeNoOp(Services.GetRequiredService<IGitOpsMutations>(), async (operation, ct) =>
        {
            if (intercepted || operation.Declaration.Address.Kind != "WebService" ||
                operation.Phase != DeclarationPhase.Configure)
                return;
            intercepted = true;
            await Secrets.UpdateOneAsync(item => item.Id == secret.Id,
                Builders<Secret>.Update.Inc(item => item.ReferenceFence, 1), cancellationToken: ct);
        });
        var pipeline = new RepositoryPipelineDriver(Services, Fixture, repository, customMutations: mutations);
        var result = await pipeline.SubmitAsync(new(TestData.TenantId, source), Snapshot());
        Assert.True(intercepted);
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        Assert.Equal(service.Revision, (await Service()).Revision);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(releases, await Releases());
        var operations = await Services.GetRequiredService<GitRepositoryQueries>().OperationsAsync(TestData.TenantId,
            source, result.Context!.RunId, 1, 20, default);
        var plan = operations!.Items.Single(item => item.Kind == "WebService" &&
            item.Phase == DeclarationPhase.Configure)
            .Plan!;
        Assert.Equal(2, plan.Revision);
        Assert.Equal(GitPlannedAction.NoOp, plan.Action);
    }

    private sealed class BeforeNoOp(IGitOpsMutations inner,
        Func<GitOperation, CancellationToken, Task> action) : DelegatingGitOpsMutations(inner)
    {
        public override async Task<GitOperationResult> ExecuteAsync(GitOperation operation,
            Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation, CancellationToken ct)
        {
            if (operation.Plan.Decision is NoOpDeclarationPlan)
                await action(operation, ct);
            return await base.ExecuteAsync(operation, mutation, ct);
        }
    }
}
