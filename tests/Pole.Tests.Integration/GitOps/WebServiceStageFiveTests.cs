using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class DeclarativePipelineTests
{
    [Fact]
    public async Task WebService_named_scope_admits_secret_binding_and_next_run_is_no_op()
    {
        var files = StageFiveWebSnapshot(_ => { }, withSecrets: true, scope: "application-scope");
        var initial = await Apply(files);
        Assert.Equal(RepositoryPipelineStatus.Blocked, initial.Status);
        var service = await Service();
        var scope = await DbContext.IsolationScopes.Find(item => item.Name == "application-scope").SingleAsync();
        Assert.Equal(scope.Id, service.IsolationScopeId);
        Assert.IsType<PreparedWebServiceLifecycle>(service.Lifecycle);
        await Supply();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Pipeline.ResumeAsync(initial.Context!)).Status);
        var configured = await Service();
        var events = await BusinessEvents();
        var releases = await Releases();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        Assert.Equal(configured.Revision, (await Service()).Revision);
        Assert.Equal(events, await BusinessEvents());
        Assert.Equal(releases, await Releases());
    }

    [Fact]
    public async Task WebService_named_scope_is_immutable_after_creation()
    {
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Apply(StageFiveWebSnapshot(_ => { }))).Status);
        var initial = await Service();
        var result = await Apply(StageFiveWebSnapshot(_ => { }, scope: "different-scope"));
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(initial.IsolationScopeId, (await Service()).IsolationScopeId);
        Assert.Equal(initial.Revision, (await Service()).Revision);
    }

    [Fact]
    public async Task WebService_public_exposure_creates_generated_ingress_and_repairs_then_no_ops()
    {
        await EnablePublicWebTargetAsync();
        var files = StageFiveWebSnapshot(spec => spec["exposure"] = "Public");
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var service = await Service();
        Assert.Equal(WebServiceExposureMode.Public, service.Exposure.Mode);
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        Assert.NotNull(placement.ResolvedPlacement.Ingress);
        Assert.Single(placement.ResolvedPlacement.Ingress.Hostnames);
        Assert.EndsWith(".apps.test.local", placement.ResolvedPlacement.Ingress.Hostnames.Single());
        var releases = await Releases();
        var events = await BusinessEvents();
        await Apply(files);
        Assert.Equal(releases, await Releases());
        Assert.Equal(events, await BusinessEvents());
        await WebServices.ReplaceOneAsync(item => item.Id == service.Id, service with
        {
            Exposure = service.Exposure with { Mode = WebServiceExposureMode.Private },
            Revision = service.Revision + 1
        });
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        Assert.Equal(WebServiceExposureMode.Public, (await Service()).Exposure.Mode);
        Assert.Equal(releases + 1, await Releases());
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied,
            (await Apply(StageFiveWebSnapshot(spec => spec["exposure"] = "Private"))).Status);
        var privatePlacement = await DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync();
        Assert.Null(privatePlacement.ResolvedPlacement.Ingress);
    }

    [Fact]
    public async Task WebService_public_retry_rolls_back_generated_domain_side_effects()
    {
        await EnablePublicWebTargetAsync();
        rejectNextWebServiceRevision = true;
        var result = await Apply(StageFiveWebSnapshot(spec => spec["exposure"] = "Public"));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, result.Status);
        Assert.Equal(1, await Releases());
        Assert.Equal(1, await Fixture.Database.GetCollection<MongoDB.Bson.BsonDocument>("TenantDomainZones")
            .CountDocumentsAsync(Builders<MongoDB.Bson.BsonDocument>.Filter.Empty));
        Assert.Equal(1, await Fixture.Database.GetCollection<MongoDB.Bson.BsonDocument>("HostnameBindings")
            .CountDocumentsAsync(Builders<MongoDB.Bson.BsonDocument>.Filter.Empty));
        Assert.False(rejectNextWebServiceRevision);
    }

    [Fact]
    public async Task WebService_public_exposure_requires_current_target_ingress_capability()
    {
        var result = await Apply(StageFiveWebSnapshot(spec => spec["exposure"] = "Public"));
        Assert.Equal(RepositoryPipelineStatus.Blocked, result.Status);
        Assert.Equal(0, await WebServices.CountDocumentsAsync(Builders<WebService>.Filter.Empty));
        Assert.Equal(0, await Releases());
    }

    [Fact]
    public async Task WebService_autoscaling_repairs_managed_policy_without_reacting_to_runtime_observations()
    {
        var files = StageFiveWebSnapshot(spec => spec["autoscaling"] = JsonNode.Parse("""
            {"minReplicas":2,"maxReplicas":4,"metric":{"type":"cpu","targetAverageUtilization":70}}
            """));
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var service = await Service();
        Assert.Equal(new AutoscalingSpec(2, 4, new("cpu", 70)), service.Spec.Autoscaling);
        var releases = await Releases();
        await WebServices.ReplaceOneAsync(item => item.Id == service.Id, service with
        {
            Spec = service.Spec with { Autoscaling = new(1, 3, new("memory", 50)) },
            Revision = service.Revision + 1
        });
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply(files)).Status);
        var repaired = await Service();
        Assert.Equal(service.Spec.Autoscaling, repaired.Spec.Autoscaling);
        Assert.Equal(releases + 1, await Releases());
        var placement = await DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        await DbContext.ResourceDeployments.ReplaceOneAsync(item => item.Id == placement.Id, placement with
        {
            ObservedGeneration = placement.DesiredGeneration,
            State = new AppliedResourceDeploymentState(placement.DesiredGeneration, DateTime.UtcNow)
        });
        var events = await BusinessEvents();
        await Apply(files);
        Assert.Equal(repaired.Revision, (await Service()).Revision);
        Assert.Equal(releases + 1, await Releases());
        Assert.Equal(events, await BusinessEvents());
    }

    [Theory]
    [InlineData("replicas-and-autoscaling")]
    [InlineData("invalid-autoscaling")]
    [InlineData("fixedReplicas")]
    [InlineData("public-udp")]
    public async Task WebService_invalid_stage_five_configuration_rejects_entire_snapshot(string scenario)
    {
        var files = StageFiveWebSnapshot(spec =>
        {
            if (scenario is "replicas-and-autoscaling" or "invalid-autoscaling")
                spec["autoscaling"] = JsonNode.Parse("""
                    {"minReplicas":1,"maxReplicas":4,"metric":{"type":"cpu","targetAverageUtilization":70}}
                    """);
            if (scenario == "replicas-and-autoscaling")
                spec["replicas"] = 2;
            if (scenario == "invalid-autoscaling")
                spec["autoscaling"]!["maxReplicas"] = 0;
            if (scenario == "fixedReplicas")
                spec["fixedReplicas"] = 5;
            if (scenario == "public-udp")
            {
                spec["exposure"] = "Public";
                spec["ports"]![0]!["protocol"] = "UDP";
            }
        });
        Assert.Equal(RepositoryPipelineStatus.InvalidSnapshot, (await Apply(files)).Status);
        Assert.Equal(0, await Inventory.CountDocumentsAsync(Builders<ManagedDeclaration>.Filter.Empty));
    }

    private async Task EnablePublicWebTargetAsync()
    {
        var targets = Fixture.Database.GetCollection<DeploymentTarget>("DeploymentTargets");
        var target = await targets.Find(item => item.Id == TestData.DefaultDeploymentTargetId).SingleAsync();
        target = (target with
        {
            WorkloadConfiguration = DeploymentTargetWorkloadConfiguration.Create(
                DeploymentTargetIngressConfiguration.Create("traefik", "letsencrypt-prod", "apps.test.local"),
                new(ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add("public-ingress",
                    DeploymentTargetCidrNetworkSource.Create("192.0.2.0/24")))),
            Revision = target.Revision + 1
        }).AttestForTests();
        await targets.ReplaceOneAsync(item => item.Id == target.Id, target);
    }

    private static ManifestFile[] StageFiveWebSnapshot(Action<JsonNode> configure, bool withSecrets = false,
        string? scope = null)
    {
        var documents = Snapshot()[0].Content.Split("\n---\n").Select(item => JsonNode.Parse(item)!).ToList();
        var service = documents.Single(item => item["kind"]!.GetValue<string>() == "WebService")["spec"]!;
        if (!withSecrets)
        {
            documents.RemoveAll(item => item["kind"]!.GetValue<string>() is "Secret" or "SecretBinding");
            service["secretEnvironment"] = new JsonObject();
        }
        if (scope is not null)
        {
            service["placement"]!["isolationScope"] = scope;
            foreach (var secret in documents.Where(item => item["kind"]!.GetValue<string>() == "Secret"))
                secret["spec"]!["isolationScope"] = scope;
            documents.Add(new JsonObject
            {
                ["apiVersion"] = "pole.io/v1alpha1",
                ["kind"] = "IsolationScope",
                ["metadata"] = new JsonObject { ["name"] = scope },
                ["spec"] = new JsonObject { ["resourceGroup"] = "demo" }
            });
        }
        configure(service);
        return [new("stage-five.yaml", string.Join("\n---\n", documents.Select(item => item.ToJsonString())))];
    }
}
