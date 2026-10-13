using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Application.Persistence.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageObservationApiTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    private readonly List<string> reads = [];
    private readonly List<IDisposable> clients = [];
    private long connectionVersion = 1;
    private bool enabled = true;
    private bool rotateDuringRead;
    private bool ownerMetadata;
    private string provisioner = "driver.example";
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<StorageObservationOptions>(); services.AddSingleton(new StorageObservationOptions { Enabled = true });
        services.RemoveAll<LookupDeploymentTarget>();
        services.AddSingleton<LookupDeploymentTarget>((id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(new DeploymentTargetLookupDto(id, "Target", "cluster", "InCluster", connectionVersion,
            new(1, null, new(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)), 1, enabled, false, false, "Healthy")));
        // Each target client receives a fresh owned handler; tests use the real Kubernetes SDK.
        var handler = new NativeWatchTestHandler(Response, _ => throw new InvalidOperationException("HTTP discovery cannot open a watch."));
        var client = KubernetesNativeWatchTests.Client(handler); clients.Add(client);
        services.RemoveAll<IKubernetesClientProvider>(); services.AddSingleton<IKubernetesClientProvider>(new FixedKubernetesClientProvider(client));
    }

    [Fact]
    public async Task CoherentEnvelopeRequiresAdminAndNeverIncludesNativeParametersOrMetadata()
    {
        var path = $"/api/v1/platform/storage/observation?deploymentTargetId={TestData.DefaultDeploymentTargetId}";
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync(path)).StatusCode); Assert.Empty(reads);
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var response = await Client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("native-canary", payload); Assert.DoesNotContain("parameters", payload); Assert.DoesNotContain("metadata", payload);
        var snapshot = JsonSerializer.Deserialize<StorageObservationSnapshot>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(TestData.DefaultDeploymentTargetId, snapshot.DeploymentTargetId); Assert.Equal(1, snapshot.ConnectionVersion); Assert.Null(snapshot.ObservationVersion); Assert.True(snapshot.Complete);
        Assert.Single(snapshot.StorageClasses); Assert.Single(snapshot.PersistentVolumes); Assert.Single(snapshot.PersistentVolumeClaims); Assert.Equal(3, reads.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/platform/storage/observation?deploymentTargetId=invalid")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrRotatedTargetCannotReturnOldNativeEvidence(bool rotate)
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        enabled = rotate; rotateDuringRead = rotate;
        var response = await Client.GetAsync($"/api/v1/platform/storage/observation?deploymentTargetId={TestData.DefaultDeploymentTargetId}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("native-canary", await response.Content.ReadAsStringAsync());
        if (!rotate) Assert.Empty(reads);
    }

    [Fact]
    public async Task MaximumQualifiedProvisionerRemainsCompleteThroughNativeSdkAndAdminApi()
    {
        provisioner = StorageNativeApi.MaximumQualifiedProvisioner; Assert.Equal(317, provisioner.Length);
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var response = await Client.GetAsync($"/api/v1/platform/storage/observation?deploymentTargetId={TestData.DefaultDeploymentTargetId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("native-canary", payload);
        var snapshot = JsonSerializer.Deserialize<StorageObservationSnapshot>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(snapshot.Complete); Assert.Equal(provisioner, Assert.Single(snapshot.StorageClasses).Provisioner); Assert.Equal(3, reads.Count);
    }

    [Fact]
    public async Task UnicodeOwnerDisplayNameSurvivesWhileMalformedOrOversizedOptionalMetadataIsOmitted()
    {
        ownerMetadata = true; Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var response = await Client.GetAsync($"/api/v1/platform/storage/observation?deploymentTargetId={TestData.DefaultDeploymentTargetId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("native-canary", payload);
        var snapshot = JsonSerializer.Deserialize<StorageObservationSnapshot>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(snapshot.Complete); var owner = Assert.Single(snapshot.PersistentVolumeClaims).Owner;
        Assert.Equal("Мій кластер", owner.ResourceName); Assert.Null(owner.ResourceId); Assert.Null(owner.TenantId);
        Assert.Equal(TestData.TenantId.ToLowerInvariant(), owner.ResourceGroupId);
    }

    [Fact]
    public async Task AdminReadUsesCurrentCommittedSnapshotWithoutNativeLists()
    {
        var id = TestData.DefaultDeploymentTargetId;
        await Fixture.Database.GetCollection<BsonDocument>("DeploymentTargets").InsertOneAsync(new BsonDocument
        { { "_id", ObjectId.Parse(id) }, { "Enabled", true }, { "DeletionRequestedAtUtc", BsonNull.Value }, { "Connection", new BsonDocument("Version", 1L) }, { "ObservationFence", 0L } });
        var store = new MongoStorageObservationStore(Fixture.Database, new Pole.Database.MongoRunTransaction(Fixture.Client),
            (request, session, ct) => new Pole.Features.DeploymentTargets.Application.Persistence.Admission.MongoFenceDeploymentTargetObservation(Fixture.DbContext.DeploymentTargets).InvokeAsync(request, session, ct), Fixture.StoreOutboxWrites, new());
        var context = new StorageObservationContext(id, 1);
        await store.PutDemandAsync(new(new(id, "subscription", "connection", "session"), DateTimeOffset.UtcNow.AddMinutes(10)), context);
        var owner = Assert.IsType<StorageObservationOwnership>((await store.ClaimLeaseAsync(context, "replica-a")).Value);
        var source = Assert.IsType<StorageObservationSourceState>((await store.SetSourceStateAsync(owner.Lease, context, owner.SourceControlVersion, true, "source-a")).Value);
        var committed = Assert.IsType<StorageObservationSnapshot>((await store.TryCommitSnapshotAsync(owner.Lease, context, source.SourceControlVersion, "source-a", new([], [], [], true))).Value);
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var response = await Client.GetFromJsonAsync<StorageObservationSnapshot>($"/api/v1/platform/storage/observation?deploymentTargetId={id}");
        Assert.Equal(committed.ObservationVersion, response!.ObservationVersion); Assert.Equal(committed.CheckedAtUtc, response.CheckedAtUtc); Assert.True(response.Complete); Assert.Empty(reads);
    }

    private string Response(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath; reads.Add(path);
        if (rotateDuringRead) connectionVersion = 2;
        if (path.EndsWith("storageclasses", StringComparison.Ordinal))
        {
            var classes = JsonNode.Parse("""
            {"apiVersion":"storage.k8s.io/v1","kind":"StorageClassList","metadata":{"resourceVersion":"classes-version"},"items":[{"metadata":{"name":"class-a","uid":"class-uid","labels":{"app.kubernetes.io/raw":"native-canary"},"annotations":{"pole.io/raw":"native-canary"}},"provisioner":"driver.example","parameters":{"raw":"native-canary"}}]}
            """)!;
            classes["items"]![0]!["provisioner"] = provisioner;
            return classes.ToJsonString();
        }
        if (path.EndsWith("persistentvolumes", StringComparison.Ordinal)) return """
            {"apiVersion":"v1","kind":"PersistentVolumeList","metadata":{"resourceVersion":"volumes-version"},"items":[{"metadata":{"name":"volume-a","uid":"volume-uid"},"spec":{"storageClassName":"class-a","capacity":{"storage":"1Gi"},"csi":{"driver":"driver.example","volumeHandle":"native-canary"}},"status":{"phase":"Bound"}}]}
            """;
        var claims = """
            {"apiVersion":"v1","kind":"PersistentVolumeClaimList","metadata":{"resourceVersion":"claims-version"},"items":[{"metadata":{"name":"claim-a","namespace":"owned","uid":"claim-uid"},"spec":{"storageClassName":"class-a","resources":{"requests":{"storage":"1Gi"}}},"status":{"phase":"Bound"}}]}
            """;
        if (!ownerMetadata) return claims;
        var json = JsonNode.Parse(claims)!;
        json["items"]![0]!["metadata"]!["annotations"] = new JsonObject
        {
            [PoleStorageLabels.ResourceName] = "Мій кластер",
            [PoleStorageLabels.ResourceId] = "native-canary" + new string('x', 300),
            [PoleStorageLabels.TenantId] = "malformed\nidentity",
            [PoleStorageLabels.ResourceGroupId] = TestData.TenantId.ToUpperInvariant(),
            ["pole.io/arbitrary"] = "native-canary"
        };
        return json.ToJsonString();
    }
    public override async Task DisposeAsync()
    {
        await base.DisposeAsync(); foreach (var client in clients) client.Dispose();
    }
}
