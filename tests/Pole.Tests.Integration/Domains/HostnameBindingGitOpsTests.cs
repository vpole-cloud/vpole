using Pole.Features.GitOps.Compiler;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Domains.Migrations;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    private IMongoCollection<HostnameBinding> DomainBindings =>
        Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings");

    private static ManifestFile[] HostnameSnapshot(string relativeName = "api") =>
    [
        .. DomainZoneSnapshot(),
        new("hostname.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: HostnameBinding
            metadata:
              name: api-hostname
            spec:
              zone: company-zone
              resource: {kind: WebService, name: demo-api}
              deploymentTarget: {{NamedCatalogTestData.TargetName}}
              relativeName: {{relativeName}}
            """)
    ];

    [Fact]
    public async Task HostnameBindingAttachesAcrossSourcesRepairsIngressThenNoOpsWithoutAdoptingWorkload()
    {
        await EnablePublicWebTargetAsync();
        await new DomainIndexesMigration().Up(Fixture.Database);
        var workloadSource = Id();
        var workload = StageFiveWebSnapshot(spec => spec["exposure"] = "Public");
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(workload, workloadSource)).Status);
        var service = await Service();
        var releaseCount = await Releases();
        var accepted = await Apply(HostnameSnapshot());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, accepted.Status);
        var binding = await DomainBindings.Find(
            Builders<HostnameBinding>.Filter.Eq("Identity._t", nameof(NamedDomainIdentity))).SingleAsync();
        Assert.Equal(service.Id, binding.ResourceId);
        Assert.Equal("api.company.apps.test.local", binding.Hostname);
        var listing = (await Mediator.Send(new GetWebServiceDeploymentHostnamesRequest(service.Id,
            binding.ResourceDeploymentId, TestData.TenantId))).AssertHandlerOutcome();
        var listed = Assert.IsAssignableFrom<IReadOnlyList<WebServiceHostnameBindingDto>>(listing.Value);
        Assert.Equal(source, listed.Single(item => item.Id == binding.Id).GitOps!.SourceId);
        Assert.Equal(releaseCount + 1, await Releases());
        var release = await Fixture.Database.GetCollection<WebServiceRelease>("WebServiceReleases").Find(item =>
            item.WebServiceId == service.Id && item.Action == WebServiceReleaseAction.HostnameChanged).SingleAsync();
        var provenance = Assert.IsType<GitOpsReleaseProvenance>(release.Provenance);
        Assert.Equal(source, provenance.SourceId);
        Assert.Equal(accepted.Context!.RunId, provenance.RunId);
        Assert.Equal(accepted.Context.Authority.Commit, provenance.CommitSha);
        Assert.Null(release.ActorUserId);
        Assert.Null(release.ActorEmail);
        Assert.Equal(workloadSource, (await Inventory.Find(item => item.Kind == "WebService").SingleAsync()).SourceId);
        Assert.Equal(source, (await Inventory.Find(item => item.Kind == "HostnameBinding").SingleAsync()).SourceId);
        var placement = await DbContext.ResourceDeployments.Find(item => item.Id == binding.ResourceDeploymentId)
            .SingleAsync();
        Assert.Contains(binding.Hostname, placement.ResolvedPlacement.Ingress!.Hostnames);
        var events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(HostnameSnapshot())).Status);
        Assert.Equal(releaseCount + 1, await Releases());
        Assert.Equal(events, await BusinessEvents());

        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, placement with
        {
            Revision = placement.Revision + 1,
            ResolvedPlacement = placement.ResolvedPlacement with
            {
                Ingress = placement.ResolvedPlacement.Ingress with
                {
                    Hostnames = placement.ResolvedPlacement.Ingress.Hostnames.Remove(binding.Hostname)
                }
            }
        });
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(HostnameSnapshot())).Status);
        var repaired = await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync();
        Assert.Contains(binding.Hostname, repaired.ResolvedPlacement.Ingress!.Hostnames);
        Assert.Equal(releaseCount + 2, await Releases());
        events = await BusinessEvents();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(HostnameSnapshot())).Status);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(releaseCount + 2, await Releases());

        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(workload, workloadSource)).Status);
        Assert.Contains(binding.Hostname, (await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id)
            .SingleAsync()).ResolvedPlacement.Ingress!.Hostnames);
        Assert.Equal(RepositoryPipelineStatus.Blocked,
            (await Apply(StageFiveWebSnapshot(spec => spec["exposure"] = "Private"), workloadSource)).Status);
        Assert.Equal(WebServiceExposureMode.Public, (await Service()).Exposure.Mode);

        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var delete = await Services.GetRequiredService<DeleteCustomHostnameBinding>()(new(binding.TenantId,
            Pole.Workloads.ResourceTypeId.Create(binding.ResourceType), binding.ResourceId,
            binding.ResourceDeploymentId, binding.Id), session, default);
        Assert.IsType<CustomHostnameBindingGitOwned>(delete.Value);
        await session.AbortTransactionAsync();
    }

    [Fact]
    public async Task HostnameBindingMissingCreatedAndImmutableAddressesAreBlockedWithoutRecreation()
    {
        await EnablePublicWebTargetAsync();
        await Apply(StageFiveWebSnapshot(spec => spec["exposure"] = "Public"), Id());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(HostnameSnapshot())).Status);
        var binding = await DomainBindings.Find(
            Builders<HostnameBinding>.Filter.Eq("Identity._t", nameof(NamedDomainIdentity))).SingleAsync();
        var releases = await Releases();
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(HostnameSnapshot("other"))).Status);
        await DomainBindings.DeleteOneAsync(item => item.Id == binding.Id);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(HostnameSnapshot())).Status);
        Assert.Equal(0, await DomainBindings.CountDocumentsAsync(item => item.Id == binding.Id));
        await DomainBindings.InsertOneAsync(binding with { Id = Id() });
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply(HostnameSnapshot())).Status);
        Assert.Equal(0, await DomainBindings.CountDocumentsAsync(item => item.Id == binding.Id));
        Assert.Equal(releases, await Releases());
    }

    [Fact]
    public async Task HostnameBindingSameSnapshotCreatesAfterPublicWorkloadConfiguration()
    {
        await EnablePublicWebTargetAsync();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Apply([.. StageFiveWebSnapshot(spec => spec["exposure"] = "Public"),
                .. HostnameSnapshot()])).Status);
        var bindings = await DomainBindings.Find(
            Builders<HostnameBinding>.Filter.Eq("Identity._t", nameof(NamedDomainIdentity))).ToListAsync();
        Assert.Single(bindings);
        var service = await Service();
        Assert.Equal(service.Id, bindings[0].ResourceId);
        var inventory = await Inventory.Find(item => item.Kind == "HostnameBinding").SingleAsync();
        Assert.Equal(GitResourceMaterialization.Created, inventory.Materialization);
        Assert.NotNull(inventory.Projections.Applied);
    }
}
