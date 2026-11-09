using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Outbox.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Migrations;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

[Collection(IntegrationTestCollection.Global3)]
public sealed class WebServiceSpecValidationApiTests(MongoReplicaSetFixture fixture) : IsolatedApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_and_update_reject_out_of_schema_specs_before_any_persisted_side_effects()
    {
        await ApplyCurrentWebServiceSchema();
        var group = await Fixture.DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var body = Data.CreateWebServiceRequest(group.Id).Body with { Exposure = WebServiceExposureMode.Private };
        using var createdResponse = await Client.PostAsJsonAsync("/api/v1/web-services", body);
        Assert.Equal(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<CreateWebServiceResponse>())!;
        var original = await Fixture.Database.WebServices().Find(item => item.Id == created.WebServiceId).SingleAsync();
        var writes = await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty);
        var releases = await Fixture.Database.WebServiceReleases().CountDocumentsAsync(item =>
            item.WebServiceId == created.WebServiceId);

        foreach (var (spec, field) in InvalidSpecs(body.Spec!))
        {
            using var create = await Client.PostAsJsonAsync("/api/v1/web-services",
                body with { Name = "invalid-spec", Spec = spec });
            await AssertValidationError(create, "Body.Spec." + field);
            using var update = await Client.PutAsJsonAsync($"/api/v1/web-services/{created.WebServiceId}",
                new UpdateWebServiceRequestBody(spec, body.Tags, body.Exposure, original.Revision));
            await AssertValidationError(update, "Body.Spec." + field);
        }

        var current = await Fixture.Database.WebServices().Find(item => item.Id == original.Id).SingleAsync();
        Assert.Equal(original.ToBsonDocument(), current.ToBsonDocument());
        Assert.Equal(1, await Fixture.Database.WebServices().CountDocumentsAsync(item =>
            item.TenantId == Data.TenantId));
        Assert.Equal(writes,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        Assert.Equal(releases, await Fixture.Database.WebServiceReleases().CountDocumentsAsync(item =>
            item.WebServiceId == created.WebServiceId));
    }

    [Fact]
    public async Task Persisted_probe_boundaries_and_named_ports_are_accepted_on_create_and_update()
    {
        await ApplyCurrentWebServiceSchema();
        var group = await Fixture.DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var probe = new ProbeConfigDto("/" + new string('a', 199), "http", 300, 120, 60, 10, 1);
        var spec = TestData.ValidWebServiceSpec() with
        {
            Probes = new(probe, probe, probe with { SuccessThreshold = 10 })
        };
        var body = Data.CreateWebServiceRequest(group.Id).Body with
        {
            Spec = spec,
            Exposure = WebServiceExposureMode.Private
        };
        using var response = await Client.PostAsJsonAsync("/api/v1/web-services", body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateWebServiceResponse>())!;
        var updatedSpec = spec with { Probes = spec.Probes with { Startup = probe with { FailureThreshold = 1 } } };
        using var updated = await Client.PutAsJsonAsync($"/api/v1/web-services/{created.WebServiceId}",
            new UpdateWebServiceRequestBody(updatedSpec, body.Tags, body.Exposure, ExpectedRevision: 1));
        Assert.Equal(HttpStatusCode.Accepted, updated.StatusCode);
        var saved = await Fixture.Database.WebServices().Find(item => item.Id == created.WebServiceId).SingleAsync();
        Assert.Equal("http", saved.Spec.Probes.Startup.Port);
        Assert.Equal(1, saved.Spec.Probes.Startup.FailureThreshold);
        Assert.Equal(10, saved.Spec.Probes.Readiness.SuccessThreshold);
    }

    private async Task ApplyCurrentWebServiceSchema()
    {
        await new WebServiceSchemaValidationMigration().Up(Fixture.Database);
        await new WebServiceDependencyReferencesSchemaMigration().Up(Fixture.Database);
    }

    private static async Task AssertValidationError(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _), field);
    }

    private static IEnumerable<(WebServiceSpecDto Spec, string Field)> InvalidSpecs(WebServiceSpecDto spec)
    {
        foreach (var phase in new[] { "Startup", "Liveness", "Readiness" })
        {
            foreach (var (probe, field) in InvalidProbes(spec.Probes.Startup))
                yield return (spec with { Probes = ChangeProbe(spec.Probes, phase, probe) }, $"Probes.{phase}.{field}");
            yield return (spec with { Probes = ChangeProbe(spec.Probes, phase, null!) }, "Probes." + phase);
        }

        yield return (spec with { Probes = null! }, "Probes");
        yield return (spec with { Config = null! }, "Config");
        yield return (spec with { Image = spec.Image! with { Server = new string('a', 256) } }, "Image.Server");
        yield return (spec with { Image = spec.Image! with { Name = new string('a', 256) } }, "Image.Name");
        yield return (spec with { Image = spec.Image! with { Tag = new string('a', 101) } }, "Image.Tag");
        yield return (spec with { Config = spec.Config with { PortMappings = null! } }, "Config.PortMappings");
        yield return (spec with { Config = spec.Config with { PortMappings = [null!] } }, "Config.PortMappings[0]");

        var port = spec.Config.PortMappings[0];
        foreach (var (mapping, field) in new (PortMappingDto Mapping, string Field)[]
                 {
                     (port with { ServicePort = 0 }, "ServicePort"),
                     (port with { ServicePort = 65536 }, "ServicePort"),
                     (port with { TargetPort = "" }, "TargetPort"),
                     (port with { TargetPort = new string('a', 11) }, "TargetPort"),
                     (port with { Protocol = "tcp" }, "Protocol"),
                     (port with { Protocol = null! }, "Protocol"),
                     (port with { Name = null! }, "Name")
                 })
            yield return (spec with { Config = spec.Config with { PortMappings = [mapping] } },
                "Config.PortMappings[0]." + field);
    }

    private static ProbeSpecDto ChangeProbe(ProbeSpecDto probes, string phase, ProbeConfigDto value) => phase switch
    {
        "Startup" => probes with { Startup = value },
        "Liveness" => probes with { Liveness = value },
        "Readiness" => probes with { Readiness = value },
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    private static IEnumerable<(ProbeConfigDto Probe, string Field)> InvalidProbes(ProbeConfigDto probe)
    {
        yield return (probe with { Path = "" }, "Path");
        yield return (probe with { Path = new string('a', 201) }, "Path");
        yield return (probe with { Port = "" }, "Port");
        yield return (probe with { Port = new string('a', 11) }, "Port");
        yield return (probe with { InitialDelaySeconds = -1 }, "InitialDelaySeconds");
        yield return (probe with { InitialDelaySeconds = 301 }, "InitialDelaySeconds");
        yield return (probe with { PeriodSeconds = 0 }, "PeriodSeconds");
        yield return (probe with { PeriodSeconds = 121 }, "PeriodSeconds");
        yield return (probe with { TimeoutSeconds = 0 }, "TimeoutSeconds");
        yield return (probe with { TimeoutSeconds = 61 }, "TimeoutSeconds");
        yield return (probe with { FailureThreshold = 0 }, "FailureThreshold");
        yield return (probe with { FailureThreshold = 11 }, "FailureThreshold");
        yield return (probe with { SuccessThreshold = 0 }, "SuccessThreshold");
        yield return (probe with { SuccessThreshold = 11 }, "SuccessThreshold");
    }
}
