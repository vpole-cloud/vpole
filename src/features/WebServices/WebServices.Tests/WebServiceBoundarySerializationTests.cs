using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Xunit;

namespace Pole.Features.WebServices.Tests;

public sealed class WebServiceBoundarySerializationTests
{
    [Fact]
    public void Pending_image_DTO_has_no_placeholder_and_request_metadata_cannot_supply_delivery_authority()
    {
        var configurationId = ObjectId.GenerateNewId().ToString();
        var spec = WebServiceTestData.CreateSpec().ToDomain() with
        {
            Image = WebServiceImageFactory.Pending(configurationId)
        };
        var dto = spec.ToDto();
        Assert.Null(dto.Image);
        Assert.Equal("PendingSourceBuild", dto.ImageAuthority!.Mode);
        Assert.Equal(configurationId, dto.ImageAuthority.ConfigurationId);
        var restored = JsonSerializer.Deserialize<WebServiceSpecDto>(JsonSerializer.Serialize(dto))!;
        Assert.Null(restored.Image);
        Assert.Equal(dto.ImageAuthority, restored.ImageAuthority);
        Assert.IsType<UnboundSourceBuildImage>(restored.ToDomain().Image);

        var direct = WebServiceTestData.CreateSpec().ToDomain();
        var artifact = DeliveredSourceBuildImage.Create(configurationId, Assert.IsType<ImageInfo>(direct.Image),
            "sha256:" + new string('a', 64), ObjectId.GenerateNewId().ToString(), 1);
        var deliveredDto = (direct with { Image = artifact }).ToDto();
        Assert.Equal(artifact.Digest, deliveredDto.ImageAuthority!.Digest);
        Assert.IsType<ImageInfo>(deliveredDto.ToDomain().Image);
        Assert.DoesNotContain("secretName", JsonSerializer.Serialize(deliveredDto), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Logical_references_round_trip_HTTP_BSON_and_legacy_missing_fields_default_empty()
    {
        var id = ObjectId.GenerateNewId().ToString();
        var config = new WebServiceConfigDto()
        {
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty,
            PortMappings = [],
            ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReferenceDto>.Empty.Add("PGHOST", new(id, "Host")),
            SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReferenceDto>.Empty.Add("PGPASSWORD", new(id, "password"))
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<WebServiceConfigDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var domain = restored.ToDomain();
        var bson = domain.ToBsonDocument();
        var roundTrip = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<WebServiceConfig>(bson);
        Assert.Equal(id, roundTrip.ConnectionEnvironmentReferences["PGHOST"].ConnectionId);
        Assert.Equal("password", roundTrip.SecretEnvironmentReferences["PGPASSWORD"].Key);
        Assert.DoesNotContain("KubernetesName", bson.ToJson());
        bson.Remove("ConnectionEnvironmentReferences"); bson.Remove("SecretEnvironmentReferences");
        var legacy = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<WebServiceConfig>(bson);
        Assert.Empty(legacy.ConnectionEnvironmentReferences); Assert.Empty(legacy.SecretEnvironmentReferences);
        var absent = JsonSerializer.Deserialize<WebServiceConfigDto>("{\"environmentVariables\":{},\"portMappings\":[]}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(absent.ToDomain().SecretEnvironmentReferences);
    }

    [Theory]
    [InlineData("collision")]
    [InlineData("native-name")]
    [InlineData("invalid-kind")]
    [InlineData("unexpected-key")]
    public void Reference_validation_rejects_collisions_native_identity_and_invalid_field_shape(string failure)
    {
        var config = new WebServiceConfigDto()
        {
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty,
            PortMappings = [],
            ConnectionEnvironmentReferences = ImmutableDictionary<string,
                ConnectionEnvironmentReferenceDto>.Empty.Add("HOST", new(ObjectId.GenerateNewId().ToString(), "Host")),
            SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReferenceDto>.Empty
        };
        config = failure switch
        {
            "collision" => config with { EnvironmentVariables = config.EnvironmentVariables.Add("HOST", "literal") },
            "native-name" => config with { SecretEnvironmentReferences = config.SecretEnvironmentReferences.Add("PASSWORD", new("native-secret-name", "password")) },
            "invalid-kind" => config with { ConnectionEnvironmentReferences = config.ConnectionEnvironmentReferences.SetItem("HOST", new(ObjectId.GenerateNewId().ToString(), "Address")) },
            _ => config with { ConnectionEnvironmentReferences = config.ConnectionEnvironmentReferences.SetItem("HOST", new(ObjectId.GenerateNewId().ToString(), "Host", "database")) }
        };
        Assert.False(new WebServiceSpecDtoValidator().Validate(WebServiceTestData.CreateSpec() with { Config = config }).IsValid);
    }

    [Fact]
    public void CreateRequest_OmittedExposureDefaultsPrivate_AndNumericEnumsAreRejected()
    {
        var webJsonOptions = Pole.ASP.Helpers.PoleHttpJson.CreateSerializerOptions();
        var omitted = JsonSerializer.Deserialize<CreateWebServiceRequestBody>("{}", webJsonOptions);

        Assert.NotNull(omitted);
        Assert.Equal(WebServiceExposureMode.Private, omitted.Exposure);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateWebServiceRequestBody>(
            "{\"exposure\":1}", webJsonOptions));
    }

    [Fact]
    public void CreateRequest_PublicValidationSafelyRejectsMissingSpecAndNonTcpRoute()
    {
        var validator = new CreateWebServiceRequestBodyValidator();
        var missingSpec = new CreateWebServiceRequestBody(
            "api", "507f1f77bcf86cd799439011", null!, null!, ImmutableHashSet<string>.Empty,
            WebServiceExposureMode.Public);
        var nonTcpRoute = missingSpec with
        {
            Spec = WebServiceTestData.CreateSpec() with
            {
                Config = WebServiceTestData.CreateSpec().Config with
                {
                    PortMappings = [new PortMappingDto(80, "8080", "UDP", "http")]
                }
            }
        };

        Assert.False(validator.Validate(missingSpec).IsValid);
        Assert.False(validator.Validate(nonTcpRoute).IsValid);
    }

    [Fact]
    public void EventOwnedSnapshots_PreserveThePublishedJsonShape()
    {
        var probe = new WebServiceProbeConfigSnapshot("/health", "8080", 5, 10, 5, 3, 1);
        var spec = new WebServiceSpecSnapshot(
            new WebServiceImageInfoSnapshot("docker.io", "nginx", "1.27"),
            new WebServiceConfigSnapshot(
                ImmutableDictionary<string, string>.Empty.Add("ASPNETCORE_ENVIRONMENT", "Development"),
                [new PortMappingSnapshot(80, "8080", "TCP", "http")]),
            Autoscaling: null,
            Probes: new WebServiceProbeSpecSnapshot(probe, probe, probe));
        var @event = new WebServiceCreatedEvent(
            "web-service-1",
            "api",
            "resource-group-1",
            "isolation-scope-1",
            1,
            WebServiceLifecycleState.Active,
            spec,
            new WebServiceExposureSnapshot("Public", "api"),
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
                TenantId = WebServiceTestData.TenantId,
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
        Assert.Equal("Public", root.GetProperty("exposure").GetProperty("mode").GetString());
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
            WebServiceTestData.CreateSpec().ToDomain().Probes,
            EventExtensions.SerializerOptions);
        Assert.True(JsonNode.DeepEquals(
            previousSharedProbeShape,
            eventOwnedProbeShape));
    }

    [Fact]
    public void DomainOwnedReleaseSpec_PreservesThePersistedBsonShape()
    {
        var release = WebServiceReleaseFactory.CreatePending(
            WebServiceTestData.TenantId,
            "web-service-1",
            1,
            WebServiceReleaseAction.Create,
            "test-user",
            "test-user@example.test",
            WebServiceTestData.CreateSpec().ToDomain(),
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
                new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc)),
            WebServiceExposure.Create(WebServiceExposureMode.Public));

        var document = release.ToBsonDocument();
        var spec = document["Spec"].AsBsonDocument;
        Assert.Equal(
            release.Spec.Image,
            BsonSerializer.Deserialize<WebServiceRelease>(document).Spec.Image);
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
