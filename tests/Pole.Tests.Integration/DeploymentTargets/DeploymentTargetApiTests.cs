using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

public sealed class DeploymentTargetApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    private const string FirstToken = "eyJhbGciOiJSUzI1NiJ9.first-deployment-target-token";
    private const string RotatedToken = "eyJhbGciOiJSUzI1NiJ9.rotated-deployment-target-token";

    [Fact]
    public async Task PlatformEndpoints_RejectNonAdminUsers()
    {
        var response = await Client.GetAsync("/api/v1/platform/deployment-targets/");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListEndpoints_ReturnEligibleCreatedTarget()
    {
        SetPlatformAdmin();
        var created = await CreateTargetAsync();

        var attestResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}/network-policy",
            new UpdateDeploymentTargetNetworkPolicyBody(
                NetworkPolicyEnforcement.Supported,
                false,
                created.Revision));
        Assert.Equal(HttpStatusCode.OK, attestResponse.StatusCode);
        var platformResponse = await Client.GetAsync("/api/v1/platform/deployment-targets/");
        var availableResponse = await Client.GetAsync(
            "/api/v1/deployment-targets/available?resourceType=pole/web-service&requiresPublicIngress=false");

        Assert.Equal(HttpStatusCode.OK, platformResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, availableResponse.StatusCode);
        var targets = await platformResponse.Content
            .ReadFromJsonAsync<IReadOnlyCollection<DeploymentTargetDto>>();
        var availableTargets = await availableResponse.Content
            .ReadFromJsonAsync<IReadOnlyCollection<AvailableDeploymentTargetDto>>();
        Assert.NotNull(targets);
        Assert.NotNull(availableTargets);
        Assert.Contains(targets, target => target.Id == created.Id);
        Assert.Contains(availableTargets, target => target.Id == created.Id);
    }

    [Fact]
    public async Task RequestValidation_RejectsInvalidRouteAndBodyBeforeHandlers()
    {
        SetPlatformAdmin();
        var invalidIdResponse = await Client.GetAsync(
            "/api/v1/platform/deployment-targets/not-an-object-id");
        Assert.Equal(HttpStatusCode.BadRequest, invalidIdResponse.StatusCode);

        var invalidBodyResponse = await Client.PostAsJsonAsync(
            "/api/v1/platform/deployment-targets/",
            new CreateDeploymentTargetRequestBody(
                "Invalid admission",
                Enabled: false,
                AcceptsWorkloads: true,
                DeploymentTargetTestData.CreateWorkloadConfigurationBody(),
                new DeploymentTargetConnectionRequestBody("InCluster")));
        Assert.Equal(HttpStatusCode.BadRequest, invalidBodyResponse.StatusCode);
        Assert.Equal(0, await Fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
            Builders<DeploymentTarget>.Filter.Empty));
    }

    [Fact]
    public async Task Create_PersistsEncryptedWriteOnlyCredential_AndRejectsDuplicateClusterIdentity()
    {
        SetPlatformAdmin();
        var certificateAuthorityData = CreateCertificateAuthorityData();
        var firstResponse = await Client.PostAsJsonAsync(
            "/api/v1/platform/deployment-targets/",
            CreateBody("Cluster one", "https://cluster-one.test:6443", certificateAuthorityData, FirstToken));

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var created = await firstResponse.Content.ReadFromJsonAsync<DeploymentTargetDto>();
        Assert.NotNull(created);
        Assert.Equal("ServiceAccount", created.Connection.Kind);
        Assert.True(created.Enabled);
        Assert.True(created.AcceptsWorkloads);
        Assert.Equal(1, created.WorkloadConfiguration.Revision);
        Assert.Equal(
            "traefik",
            Assert.IsType<DeploymentTargetIngressConfigurationDto>(
                created.WorkloadConfiguration.Ingress).IngressClassName);
        var responseJson = await firstResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(FirstToken, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("encryptedCredential", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ciphertext", responseJson, StringComparison.OrdinalIgnoreCase);

        var persisted = await Fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == created.Id)
            .SingleAsync();
        var serviceAccount = Assert.IsType<ServiceAccountDeploymentTargetConnection>(persisted.Connection);
        Assert.NotEmpty(serviceAccount.EncryptedCredential.Ciphertext);
        var raw = await Fixture.Database.GetCollection<BsonDocument>("DeploymentTargets")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(created.Id)))
            .SingleAsync();
        Assert.DoesNotContain(FirstToken, raw.ToJson(), StringComparison.Ordinal);

        var duplicateResponse = await Client.PostAsJsonAsync(
            "/api/v1/platform/deployment-targets/",
            CreateBody("Duplicate cluster", "https://cluster-one.test:6443", certificateAuthorityData, RotatedToken));
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        Assert.Equal(1, await Fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
            Builders<DeploymentTarget>.Filter.Empty));
    }

    [Fact]
    public async Task AdmissionUpdate_IsRevisionFenced_AndPreventsNewPlacement()
    {
        SetPlatformAdmin();
        var created = await CreateTargetAsync();

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                created.DisplayName,
                Enabled: true,
                AcceptsWorkloads: false,
                created.WorkloadConfiguration.ToRequestBody(),
                created.Revision));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<DeploymentTargetDto>();
        Assert.NotNull(updated);
        Assert.False(updated.AcceptsWorkloads);
        Assert.Equal(created.Revision + 1, updated.Revision);

        var admission = await new MongoAdmitDeploymentTargetForWorkload(
            new MongoLoadDeploymentTargetById(Fixture.DbContext.DeploymentTargets).InvokeAsync)
            .InvokeAsync(updated.Id);
        var failure = admission switch
        {
            DeploymentTargetWorkloadAdmissionError error => error,
            _ => throw new Xunit.Sdk.XunitException("Expected deployment-target admission failure.")
        };
        Assert.True(failure is DeploymentTargetNewWorkloadsDisabled);

        var staleResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                "Stale update",
                Enabled: true,
                AcceptsWorkloads: true,
                created.WorkloadConfiguration.ToRequestBody(),
                created.Revision));
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
    }

    [Fact]
    public async Task NoOpUpdate_PreservesRevision()
    {
        SetPlatformAdmin();
        var created = await CreateTargetAsync();

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                created.DisplayName,
                created.Enabled,
                created.AcceptsWorkloads,
                created.WorkloadConfiguration.ToRequestBody(),
                created.Revision));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var unchanged = await response.Content.ReadFromJsonAsync<DeploymentTargetDto>();
        Assert.NotNull(unchanged);
        Assert.Equal(created.Revision, unchanged.Revision);
        Assert.Equal(
            created.WorkloadConfiguration.Revision,
            unchanged.WorkloadConfiguration.Revision);
    }

    [Fact]
    public async Task WorkloadConfigurationUpdate_AdvancesItsRevision_WhileMetadataUpdateDoesNot()
    {
        SetPlatformAdmin();
        var created = await CreateTargetAsync();

        var configurationResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                created.DisplayName,
                created.Enabled,
                created.AcceptsWorkloads,
                DeploymentTargetTestData.CreateWorkloadConfigurationBody(
                    "nginx",
                    "internal-ca"),
                created.Revision));

        Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
        var configured = Assert.IsType<DeploymentTargetDto>(
            await configurationResponse.Content.ReadFromJsonAsync<DeploymentTargetDto>());
        Assert.Equal(created.Revision + 1, configured.Revision);
        Assert.Equal(
            created.WorkloadConfiguration.Revision + 1,
            configured.WorkloadConfiguration.Revision);
        var ingress = Assert.IsType<DeploymentTargetIngressConfigurationDto>(
            configured.WorkloadConfiguration.Ingress);
        Assert.Equal("nginx", ingress.IngressClassName);
        Assert.Equal("internal-ca", ingress.ClusterIssuerName);

        var metadataResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                "Renamed target",
                configured.Enabled,
                configured.AcceptsWorkloads,
                configured.WorkloadConfiguration.ToRequestBody(),
                configured.Revision));

        Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        var renamed = Assert.IsType<DeploymentTargetDto>(
            await metadataResponse.Content.ReadFromJsonAsync<DeploymentTargetDto>());
        Assert.Equal(configured.Revision + 1, renamed.Revision);
        Assert.Equal(
            configured.WorkloadConfiguration.Revision,
            renamed.WorkloadConfiguration.Revision);

        var disabledIngressResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                renamed.DisplayName,
                renamed.Enabled,
                renamed.AcceptsWorkloads,
                new DeploymentTargetWorkloadConfigurationRequestBody(
                    Ingress: null,
                    NetworkSources: new DeploymentTargetNetworkSourcesRequestBody([])),
                renamed.Revision));

        Assert.Equal(HttpStatusCode.OK, disabledIngressResponse.StatusCode);
        var withoutIngress = Assert.IsType<DeploymentTargetDto>(
            await disabledIngressResponse.Content.ReadFromJsonAsync<DeploymentTargetDto>());
        Assert.Equal(renamed.Revision + 1, withoutIngress.Revision);
        Assert.Equal(
            renamed.WorkloadConfiguration.Revision + 1,
            withoutIngress.WorkloadConfiguration.Revision);
        Assert.Null(withoutIngress.WorkloadConfiguration.Ingress);
    }

    [Fact]
    public async Task NetworkSources_CreateAndUpdate_PersistTypedIdentityAndAdvanceRevision()
    {
        SetPlatformAdmin();
        var body = CreateBody(
            "Source-configured cluster",
            "https://cluster-one.test:6443",
            CreateCertificateAuthorityData(),
            FirstToken);
        var publicSource = new DeploymentTargetPodNetworkSourceRequestBody(
            "ingress-system",
            new Dictionary<string, string> { ["app.kubernetes.io/name"] = "traefik" });
        body = body with
        {
            WorkloadConfiguration = body.WorkloadConfiguration with
            {
                NetworkSources = new DeploymentTargetNetworkSourcesRequestBody(
                    new Dictionary<string, DeploymentTargetNetworkSourceRequestBody>
                    {
                        [DeploymentTargetNetworkSourcesDto.PublicIngressName] = publicSource
                    })
            }
        };

        var response = await Client.PostAsJsonAsync("/api/v1/platform/deployment-targets/", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = Assert.IsType<DeploymentTargetDto>(
            await response.Content.ReadFromJsonAsync<DeploymentTargetDto>());
        var returnedPublic = Assert.IsType<DeploymentTargetPodNetworkSourceDto>(
            created.WorkloadConfiguration.NetworkSources.Entries[DeploymentTargetNetworkSourcesDto.PublicIngressName]);
        Assert.Equal("ingress-system", returnedPublic.Namespace);
        Assert.Equal("traefik", returnedPublic.PodLabels["app.kubernetes.io/name"]);
        var persisted = await Fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == created.Id)
            .SingleAsync();
        Assert.IsType<DeploymentTargetPodNetworkSource>(
            persisted.WorkloadConfiguration.NetworkSources.Entries[DeploymentTargetNetworkSourcesDto.PublicIngressName]);

        var newSources = created.WorkloadConfiguration.NetworkSources.ToRequestBody();
        newSources.Entries.Add("example/queue", new DeploymentTargetCidrNetworkSourceRequestBody("192.0.2.9/32"));
        var update = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                created.DisplayName,
                created.Enabled,
                created.AcceptsWorkloads,
                created.WorkloadConfiguration.ToRequestBody() with { NetworkSources = newSources },
                created.Revision));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var configured = Assert.IsType<DeploymentTargetDto>(
            await update.Content.ReadFromJsonAsync<DeploymentTargetDto>());
        Assert.Equal(created.WorkloadConfiguration.Revision + 1, configured.WorkloadConfiguration.Revision);
        Assert.Equal("192.0.2.9/32", Assert.IsType<DeploymentTargetCidrNetworkSourceDto>(
            configured.WorkloadConfiguration.NetworkSources.Entries["example/queue"]).Cidr);

        var attestResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{configured.Id}/network-policy",
            new UpdateDeploymentTargetNetworkPolicyBody(
                NetworkPolicyEnforcement.Supported,
                false,
                configured.Revision));
        Assert.Equal(HttpStatusCode.OK, attestResponse.StatusCode);
        var availableResponse = await Client.GetAsync(
            "/api/v1/deployment-targets/available?resourceType=pole/web-service&requiresPublicIngress=false");
        Assert.Equal(HttpStatusCode.OK, availableResponse.StatusCode);
        var availableJson = await availableResponse.Content.ReadAsStringAsync();
        Assert.Contains(configured.Id, availableJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ingress-system", availableJson, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.9", availableJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vendor//controller")]
    [InlineData(" vendor/controller")]
    [InlineData("vendor/controller-")]
    public async Task NetworkSources_InvalidContributionKey_DoesNotChangeTarget(string sourceName)
    {
        SetPlatformAdmin();
        var created = await CreateTargetAsync();
        var update = new UpdateDeploymentTargetRequestBody(
            created.DisplayName,
            created.Enabled,
            created.AcceptsWorkloads,
            created.WorkloadConfiguration.ToRequestBody() with
            {
                NetworkSources = new DeploymentTargetNetworkSourcesRequestBody(
                    new Dictionary<string, DeploymentTargetNetworkSourceRequestBody>
                    {
                        [sourceName] = new DeploymentTargetCidrNetworkSourceRequestBody("192.0.2.9/32")
                    })
            },
            created.Revision);

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}", update);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var persisted = await Fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == created.Id)
            .SingleAsync();
        Assert.Equal(created.Revision, persisted.Revision);
        Assert.Equal(created.WorkloadConfiguration.Revision, persisted.WorkloadConfiguration.Revision);
    }

    [Fact]
    public async Task NetworkSources_RejectPaddedLabelAndUnknownJsonKind()
    {
        SetPlatformAdmin();
        var created = await CreateTargetAsync();
        var invalidSource = new DeploymentTargetPodNetworkSourceRequestBody(
            "ingress-system",
            new Dictionary<string, string> { ["app.kubernetes.io/name"] = " traefik " });
        var update = new UpdateDeploymentTargetRequestBody(
            created.DisplayName,
            created.Enabled,
            created.AcceptsWorkloads,
            created.WorkloadConfiguration.ToRequestBody() with
            {
                NetworkSources = new DeploymentTargetNetworkSourcesRequestBody(
                    new Dictionary<string, DeploymentTargetNetworkSourceRequestBody>
                    {
                        ["example/controller"] = invalidSource
                    })
            },
            created.Revision);

        var invalidLabelResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}", update);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLabelResponse.StatusCode);

        var valid = update with
        {
            WorkloadConfiguration = update.WorkloadConfiguration with
            {
                NetworkSources = new DeploymentTargetNetworkSourcesRequestBody(
                    new Dictionary<string, DeploymentTargetNetworkSourceRequestBody>
                    {
                        ["example/controller"] = invalidSource with
                        {
                            PodLabels = new Dictionary<string, string>
                            {
                                ["app.kubernetes.io/name"] = "traefik"
                            }
                        }
                    })
            }
        };
        var json = JsonSerializer.Serialize(valid, JsonSerializerOptions.Web);
        Assert.Contains("\"kind\":\"Pod\"", json, StringComparison.Ordinal);
        json = json.Replace("\"kind\":\"Pod\"", "\"kind\":\"Unknown\"", StringComparison.Ordinal);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var unknownKindResponse = await Client.PutAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}", content);
        Assert.Equal(HttpStatusCode.BadRequest, unknownKindResponse.StatusCode);

        var persisted = await Fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == created.Id)
            .SingleAsync();
        Assert.Equal(created.Revision, persisted.Revision);
    }

    [Fact]
    public async Task RequestValidation_RejectsInvalidIngressConfigurationOnCreateAndUpdate()
    {
        SetPlatformAdmin();

        var response = await Client.PostAsJsonAsync(
            "/api/v1/platform/deployment-targets/",
            new CreateDeploymentTargetRequestBody(
                "Invalid ingress",
                Enabled: true,
                AcceptsWorkloads: true,
                DeploymentTargetTestData.CreateWorkloadConfigurationBody(
                    "Invalid_Ingress",
                    "letsencrypt-prod"),
                new DeploymentTargetConnectionRequestBody("InCluster")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            0,
            await Fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
                Builders<DeploymentTarget>.Filter.Empty));

        var created = await CreateTargetAsync();
        var updateResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                created.DisplayName,
                created.Enabled,
                created.AcceptsWorkloads,
                DeploymentTargetTestData.CreateWorkloadConfigurationBody(
                    "nginx",
                    "Invalid_Issuer"),
                created.Revision));

        Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);
        var persisted = await Fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == created.Id)
            .SingleAsync();
        Assert.Equal(created.Revision, persisted.Revision);
        Assert.Equal(
            created.WorkloadConfiguration.Revision,
            persisted.WorkloadConfiguration.Revision);
    }

    [Fact]
    public async Task CredentialRotation_PreservesClusterIdentity_AndAdvancesConnectionVersion()
    {
        SetPlatformAdmin();
        var certificateAuthorityData = CreateCertificateAuthorityData();
        var created = await CreateTargetAsync(certificateAuthorityData);
        var before = await Fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == created.Id)
            .SingleAsync();
        var oldEnvelope = Assert.IsType<ServiceAccountDeploymentTargetConnection>(before.Connection).EncryptedCredential;

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}/credentials",
            new RotateDeploymentTargetCredentialsRequestBody(
                created.Revision,
                ServiceAccountConnection(
                    "https://cluster-one.test:6443",
                    certificateAuthorityData,
                    RotatedToken)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = await response.Content.ReadFromJsonAsync<DeploymentTargetDto>();
        Assert.NotNull(rotated);
        Assert.Equal(created.ClusterIdentity, rotated.ClusterIdentity);
        Assert.Equal(created.Connection.Version + 1, rotated.Connection.Version);
        Assert.Equal(created.Revision + 1, rotated.Revision);
        Assert.Equal(
            created.WorkloadConfiguration.Revision,
            rotated.WorkloadConfiguration.Revision);
        Assert.DoesNotContain(
            RotatedToken,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var after = await Fixture.DbContext.DeploymentTargets
            .Find(target => target.Id == created.Id)
            .SingleAsync();
        var newEnvelope = Assert.IsType<ServiceAccountDeploymentTargetConnection>(after.Connection).EncryptedCredential;
        Assert.NotEqual(oldEnvelope.Ciphertext, newEnvelope.Ciphertext);
    }

    [Fact]
    public async Task Delete_RejectsTheDefaultTarget()
    {
        SetPlatformAdmin();
        var created = await CreateTargetAsync();
        await Fixture.DbContext.DeploymentTargetSettings.InsertOneAsync(
            DeploymentTargetSettings.Create(created.Id, DateTime.UtcNow));

        var response = await Client.DeleteAsync($"/api/v1/platform/deployment-targets/{created.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await Fixture.DbContext.DeploymentTargets.CountDocumentsAsync(
            target => target.Id == created.Id));
    }

    private async Task<DeploymentTargetDto> CreateTargetAsync(string? certificateAuthorityData = null)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/platform/deployment-targets/",
            CreateBody(
                "Cluster one",
                "https://cluster-one.test:6443",
                certificateAuthorityData ?? CreateCertificateAuthorityData(),
                FirstToken));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<DeploymentTargetDto>(
            await response.Content.ReadFromJsonAsync<DeploymentTargetDto>());
    }

    private static CreateDeploymentTargetRequestBody CreateBody(
        string displayName,
        string serverUrl,
        string certificateAuthorityData,
        string token) =>
        new(
            displayName,
            Enabled: true,
            AcceptsWorkloads: true,
            DeploymentTargetTestData.CreateWorkloadConfigurationBody(),
            ServiceAccountConnection(serverUrl, certificateAuthorityData, token));

    private static DeploymentTargetConnectionRequestBody ServiceAccountConnection(
        string serverUrl,
        string certificateAuthorityData,
        string token) =>
        new(
            "ServiceAccount",
            ServerUrl: serverUrl,
            CertificateAuthorityData: certificateAuthorityData,
            Token: token);

    private static string CreateCertificateAuthorityData()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Pole deployment target test CA",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
    }

    private void SetPlatformAdmin() =>
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
}
