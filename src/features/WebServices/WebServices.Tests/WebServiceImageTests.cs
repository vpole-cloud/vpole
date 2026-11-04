using System.Collections.Immutable;
using System.Text.Json;
using Pole.Features.WebServices.Events;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.WebServices.Domain;
using Pole.Features.Shared.Models;
using Xunit;

namespace Pole.Features.WebServices.Tests;

public sealed class WebServiceImageTests
{
    private const string ConfigurationId = "6b800088b213917a38c38011";
    private const string JobId = "6b800088b213917a38c38012";
    private static readonly ImageInfo Direct = new("registry.example", "application", "latest", "pull-secret");
    private static readonly string Digest = "sha256:" + new string('a', 64);

    [Fact]
    public void Pending_configuration_is_inert_and_promotion_activates_an_immutable_digest()
    {
        var prepared = Service(new UnboundSourceBuildImage(), new PreparedWebServiceLifecycle());
        var configured = prepared.AcceptConfigurationAwaitingArtifact(
            prepared.Spec with { Image = WebServiceImageFactory.Pending(ConfigurationId) }, [], prepared.Exposure);
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(configured.Lifecycle);
        Assert.Null(configured.Spec.Image.ResolvedImage);
        Assert.Null(configured.Spec.Image.ResolvedImageReference);
        Assert.Same(configured,
            configured.AcceptConfigurationAwaitingArtifact(configured.Spec, [], configured.Exposure));

        var artifact = DeliveredSourceBuildImage.Create(ConfigurationId, Direct, Digest, JobId, 1);
        var active = configured.PromoteArtifact(artifact);
        Assert.IsType<ActiveWebServiceLifecycle>(active.Lifecycle);
        Assert.Equal($"registry.example/application@{Digest}", active.Spec.Image.ResolvedImageReference);
        Assert.Equal(configured.ActiveRevision + 1, active.ActiveRevision);
        Assert.Same(active, active.PromoteArtifact(artifact));
        Assert.Equal(configured.Spec.Config, active.Spec.Config);
    }

    [Fact]
    public void Artifact_promotion_rejects_stale_generation_other_binding_and_mutable_reference()
    {
        var configured = Service(WebServiceImageFactory.Pending(ConfigurationId),
            new AwaitingFirstArtifactWebServiceLifecycle());
        var artifact = DeliveredSourceBuildImage.Create(ConfigurationId, Direct, Digest, JobId, 2);
        var active = configured.PromoteArtifact(artifact);
        Assert.Throws<InvalidOperationException>(() => active.PromoteArtifact(artifact with { Generation = 1 }));
        Assert.Throws<InvalidOperationException>(() =>
            active.PromoteArtifact(artifact with { JobId = ConfigurationId }));
        Assert.Throws<InvalidOperationException>(() => configured.PromoteArtifact(
            artifact with { ConfigurationId = JobId }));
        Assert.Throws<ArgumentException>(() => DeliveredSourceBuildImage.Create(
            ConfigurationId, Direct, "latest", JobId, 1));
        Assert.Throws<InvalidOperationException>(() => active.UpdateDesiredState(
            active.Spec with { Image = Direct }, []));
    }

    [Fact]
    public void Factory_rejects_invalid_image_lifecycle_combinations()
    {
        Assert.Throws<ArgumentException>(() => Service(
            WebServiceImageFactory.Pending(ConfigurationId), new ActiveWebServiceLifecycle()));
        Assert.Throws<ArgumentException>(() => Service(new UnboundSourceBuildImage(),
            new AwaitingFirstArtifactWebServiceLifecycle()));
        Assert.Throws<ArgumentException>(() => Service(Direct, new AwaitingFirstArtifactWebServiceLifecycle()));
        var prepared = Service(WebServiceImageFactory.Pending(ConfigurationId), new PreparedWebServiceLifecycle());
        Assert.Throws<InvalidOperationException>(() => prepared.ActivatePrepared(prepared.Spec, [], prepared.Exposure));
    }

    [Fact]
    public void Current_image_variants_and_lifecycles_round_trip_without_computed_fields()
    {
        WebService[] services =
        [
            Service(Direct, new ActiveWebServiceLifecycle()),
            Service(new UnboundSourceBuildImage(), new PreparedWebServiceLifecycle()),
            Service(WebServiceImageFactory.Pending(ConfigurationId), new AwaitingFirstArtifactWebServiceLifecycle()),
            Service(DeliveredSourceBuildImage.Create(ConfigurationId, Direct, Digest, JobId, 3),
                new ActiveWebServiceLifecycle())
        ];
        foreach (var service in services)
        {
            var document = service.ToBsonDocument();
            var restored = BsonSerializer.Deserialize<WebService>(document);
            Assert.Equal(service.Spec.Image, restored.Spec.Image);
            Assert.Equal(service.Lifecycle, restored.Lifecycle);
            Assert.False(document["Spec"]["Image"].AsBsonDocument.Contains("ResolvedImage"));
            Assert.False(document["Spec"]["Image"].AsBsonDocument.Contains("ResolvedImageReference"));
        }
    }

    [Fact]
    public void Image_event_variants_round_trip_and_pending_placement_stays_inert()
    {
        WebServiceImageSnapshot[] snapshots =
        [
            new WebServiceImageInfoSnapshot("registry", "app", "v1", null),
            new UnboundSourceBuildImageSnapshot(),
            new PendingSourceBuildImageSnapshot(ConfigurationId),
            new DeliveredSourceBuildImageSnapshot(ConfigurationId,
                new WebServiceImageInfoSnapshot("registry", "app", "v1", "pull-secret"), Digest, JobId, 1)
        ];
        foreach (var snapshot in snapshots)
        {
            var json = JsonSerializer.Serialize(snapshot);
            Assert.Equal(snapshot, JsonSerializer.Deserialize<WebServiceImageSnapshot>(json));
        }

        var awaiting = Service(WebServiceImageFactory.Pending(ConfigurationId),
            new AwaitingFirstArtifactWebServiceLifecycle());
        var updated = awaiting.AdvancePlacementDesiredState();
        Assert.IsType<AwaitingFirstArtifactWebServiceLifecycle>(updated.Lifecycle);
        Assert.Null(updated.Spec.Image.ResolvedImageReference);
        Assert.Throws<InvalidOperationException>(() => awaiting.Redeploy());
    }

    private static WebService Service(WebServiceImage image, WebServiceLifecycle lifecycle) => WebServiceFactory.Create(
        "application", "6b800088b213917a38c38013", "6b800088b213917a38c38014", "6b800088b213917a38c38015",
        new WebServiceSpec(image, new WebServiceConfig
        {
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty,
            PortMappings = ImmutableList<PortMapping>.Empty
        }, null, new ProbeSpec(new ProbeConfig("/", "80"), new ProbeConfig("/", "80"), new ProbeConfig("/", "80"))),
        WebServiceExposure.Create(),
        WebServiceFactory.DefaultOptions with { Lifecycle = lifecycle });
}
