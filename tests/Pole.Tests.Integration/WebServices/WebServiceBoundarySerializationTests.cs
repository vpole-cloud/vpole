using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceBoundarySerializationTests
{
    [Fact]
    public void EventOwnedSnapshots_PreserveThePublishedJsonShape()
    {
        var spec = TestData.ValidWebServiceSpecSnapshot();
        var @event = new WebServiceCreatedEvent(
            "web-service-1",
            "api",
            "resource-group-1",
            "isolation-scope-1",
            1,
            spec,
            new WebServiceExposureSnapshot("api"),
            ImmutableHashSet.Create("production"),
            [new WebServiceDesiredDeploymentSnapshot(
                "deployment-1",
                "target-1",
                1,
                1,
                "Apply",
                ["api.example.com"])])
        {
            EventCore = new EventCore
            {
                TenantId = TestData.TenantId,
                OccurredOn = new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc),
                TraceId = "trace-1",
                CorrelationId = "correlation-1"
            }
        };

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(
            @event,
            EventExtensions.SerializerOptions));
        var root = payload.RootElement;

        Assert.Equal(
            "api.example.com",
            root.GetProperty("deployments")[0].GetProperty("hostnames")[0].GetString());
        var startup = root
            .GetProperty("spec")
            .GetProperty("probes")
            .GetProperty("startup");
        Assert.Equal("/health", startup.GetProperty("path").GetString());
        Assert.Equal("8080", startup.GetProperty("port").GetString());
        Assert.Equal(5, startup.GetProperty("initialDelaySeconds").GetInt32());
        Assert.Equal(3, startup.GetProperty("failureThreshold").GetInt32());

        var eventOwnedProbeShape = JsonSerializer.SerializeToNode(
            spec.Probes,
            EventExtensions.SerializerOptions);
        var previousSharedProbeShape = JsonSerializer.SerializeToNode(
            TestData.ValidWebServiceSpec().ToDomain().Probes,
            EventExtensions.SerializerOptions);
        Assert.True(JsonNode.DeepEquals(
            previousSharedProbeShape,
            eventOwnedProbeShape));
    }

    [Fact]
    public void DomainOwnedReleaseSpec_PreservesThePersistedBsonShape()
    {
        var release = WebServiceReleaseFactory.CreatePending(
            TestData.TenantId,
            "web-service-1",
            1,
            WebServiceReleaseAction.Create,
            TestData.UserId,
            TestData.UserEmail,
            TestData.ValidWebServiceSpec().ToDomain(),
            [new WebServiceReleaseDeployment(
                "deployment-1",
                "target-1",
                1,
                1,
                WebServiceReleaseDeploymentOperation.Apply,
                WebServiceReleaseDeploymentOutcome.Pending,
                null,
                null,
                null)],
            "trace-1",
            "correlation-1",
            new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc)));

        var document = release.ToBsonDocument();
        var spec = document["Spec"].AsBsonDocument;
        Assert.Equal(
            TestData.ValidWebServiceSpecSnapshot().ToBsonDocument(),
            spec);
        var image = spec["Image"].AsBsonDocument;
        var probes = spec["Probes"].AsBsonDocument;
        var startup = probes["Startup"].AsBsonDocument;

        Assert.Equal("docker.io", image["Server"].AsString);
        Assert.False(spec.Contains("Replicas"));
        Assert.Equal("/health", startup["Path"].AsString);
        Assert.Equal("8080", startup["Port"].AsString);
        Assert.Equal(5, startup["InitialDelaySeconds"].AsInt32);
        Assert.Equal(3, startup["FailureThreshold"].AsInt32);
    }
}
