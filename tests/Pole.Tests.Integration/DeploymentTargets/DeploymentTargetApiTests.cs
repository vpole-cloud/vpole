using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
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
                created.Revision));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<DeploymentTargetDto>();
        Assert.NotNull(updated);
        Assert.False(updated.AcceptsWorkloads);
        Assert.Equal(created.Revision + 1, updated.Revision);

        var lookup = new DeploymentTargetLookup(Fixture.DbContext);
        var admission = await lookup.ResolveForNewWorkloadAsync(updated.Id);
        Assert.False(admission.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);

        var staleResponse = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{created.Id}",
            new UpdateDeploymentTargetRequestBody(
                "Stale update",
                Enabled: true,
                AcceptsWorkloads: true,
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
                created.Revision));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var unchanged = await response.Content.ReadFromJsonAsync<DeploymentTargetDto>();
        Assert.NotNull(unchanged);
        Assert.Equal(created.Revision, unchanged.Revision);
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
