using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Domains;

[Collection(IntegrationTestCollection.Global4)]
public sealed class DomainLifecycleTests(
    MongoReplicaSetFixture fixture
) : IsolatedMediatorIntegrationTestBase(fixture)
{
    private ReviewedLifecycleAuthority? _authorization;
    private RemovalCapability? _workload;

    private ReviewedLifecycleAuthority authorization =>
        _authorization ??= new ReviewedLifecycleAuthority(Fixture.Client, Data.TenantId);

    private RemovalCapability workload =>
        _workload ??= new RemovalCapability(Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings"));

    private IMongoCollection<TenantDomainZone> Zones =>
        Fixture.Database.GetCollection<TenantDomainZone>("TenantDomainZones");

    private IMongoCollection<HostnameBinding> Bindings =>
        Fixture.Database.GetCollection<HostnameBinding>("HostnameBindings");

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IGitOpsMutations>(authorization);
        services.RemoveAll<IHostnameBindingWorkloadCapability>();
        services.AddSingleton<IHostnameBindingWorkloadCapability>(workload);
        services.AddSingleton<ApplyDeploymentTargetReferenceChanges>((
            _, _,
            _) => Task.FromResult<ApplyDeploymentTargetReferenceChangesOutcome>(
            new DeploymentTargetReferencesChanged()));
    }

    private IDeclarativeResourceLifecycleProvider Provider(string kind) => Services
        .GetServices<IDeclarativeResourceLifecycleProvider>()
        .Single(item => item.Kind == kind);

    [Fact]
    public async Task Zone_removal_joins_transaction_and_rejects_generated_identity()
    {
        var zone = Zone();
        await Zones.InsertOneAsync(zone);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("DomainZone").ReadLifecycleAsync(Read(zone), session, default)).Value);
        var removal = new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version);
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("DomainZone").RemoveAsync(removal, session, default)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(1, await Zones.CountDocumentsAsync(_ => true));
        session.StartTransaction();
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("DomainZone").RemoveAsync(removal, session, default)).Value);
        await session.CommitTransactionAsync();
        Assert.Equal(0, await Zones.CountDocumentsAsync(_ => true));
        var generated = TenantDomainZoneFactory.CreateGenerated(
            Data.TenantId,
            zone.DeploymentTargetId,
            "pole-generated-test",
            DnsName.Create("apps.example.test"),
            new TenantDomainZoneFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
        await Zones.InsertOneAsync(generated);
        var blocked = Assert.IsType<DeclarationReadBlocked>(
            (await Provider("DomainZone")
                .ReadLifecycleAsync(
                    new ReadLifecycleResourceRequest(
                        Data.TenantId,
                        new GitLifecycleResourceReference(new ResourceAddress("DomainZone", "system"), generated.Id)),
                    session,
                    default)).Value);
        Assert.IsType<GitLifecycleResourceProtected>(blocked.Failure);
    }

    [Fact]
    public async Task Binding_removal_fences_live_attachment_and_rolls_back_when_owner_write_fails()
    {
        var zone = Zone();
        var binding = Binding(zone);
        await Zones.InsertOneAsync(zone);
        await Bindings.InsertOneAsync(binding);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("HostnameBinding").ReadLifecycleAsync(Read(binding), session, default)).Value);
        workload.FailRemoval = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider("HostnameBinding")
                .RemoveAsync(
                    new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version),
                    session,
                    default));
        await session.AbortTransactionAsync();
        Assert.Equal(1, await Bindings.CountDocumentsAsync(_ => true));
        Assert.Equal(zone.Revision, (await Zones.Find(_ => true).SingleAsync()).Revision);
        workload.FailRemoval = false;
        session.StartTransaction();
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("HostnameBinding")
                .RemoveAsync(
                    new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version),
                    session,
                    default)).Value);
        await session.CommitTransactionAsync();
        Assert.True(workload.SawFence);
        Assert.True(workload.SawAbsentBindingAtRemoval);
        Assert.Equal(authorization.Authority.Commit, workload.Provenance!.Commit);
        Assert.Equal(0, await Bindings.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Binding_removal_without_repository_provenance_does_not_write()
    {
        var zone = Zone();
        var binding = Binding(zone);
        await Zones.InsertOneAsync(zone);
        await Bindings.InsertOneAsync(binding);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("HostnameBinding").ReadLifecycleAsync(Read(binding), session, default)).Value);
        var outcome = await Provider("HostnameBinding")
            .RemoveAsync(
                new RemoveLifecycleResourceRequest(
                    authorization.Authority with { Commit = null },
                    snapshot.Resource,
                    snapshot.Version),
                session,
                default);
        Assert.IsType<DeclarationReadBlocked>(outcome.Value);
        await session.CommitTransactionAsync();
        Assert.Equal(1, await Bindings.CountDocumentsAsync(_ => true));
        Assert.False(workload.SawFence);
        Assert.Null(workload.Provenance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_deleting_workload_allows_reservation_cleanup_without_fabricated_attachment(
        bool deleting)
    {
        var zone = Zone();
        var binding = Binding(zone);
        await Zones.InsertOneAsync(zone);
        await Bindings.InsertOneAsync(binding);
        workload.OwnerState = deleting ? new HostnameAttachmentOwnerDeleting() : new HostnameAttachmentOwnerGone();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var snapshot = Assert.IsType<GitLifecycleResourceSnapshot>(
            (await Provider("HostnameBinding").ReadLifecycleAsync(Read(binding), session, default)).Value);
        Assert.IsType<GitLifecycleResourceRemoved>(
            (await Provider("HostnameBinding")
                .RemoveAsync(
                    new RemoveLifecycleResourceRequest(authorization.Authority, snapshot.Resource, snapshot.Version),
                    session,
                    default)).Value);
        await session.CommitTransactionAsync();
        Assert.False(workload.SawFence);
        Assert.Null(workload.Provenance);
        Assert.Equal(0, await Bindings.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Adoption_preview_pins_domain_and_workload_identity_and_all_managed_dependencies()
    {
        var target = (TestData.SeedDeploymentTarget(ObjectId.GenerateNewId().ToString()) with
        {
            WorkloadConfiguration = DeploymentTargetWorkloadConfiguration.Create(
                DeploymentTargetIngressConfiguration.Create("traefik", "issuer", "apps.example.test"),
                new DeploymentTargetNetworkSources(
                    ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add(
                        "public-ingress",
                        DeploymentTargetCidrNetworkSource.Create("192.0.2.0/24")))),
        }).AttestForTests();
        await DbContext.DeploymentTargets.InsertOneAsync(target);
        var zone = Zone() with { DeploymentTargetId = target.Id };
        var binding = Binding(zone);
        workload.DeploymentId = binding.ResourceDeploymentId;
        await Zones.InsertOneAsync(zone);
        await Bindings.InsertOneAsync(binding);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var zoneDeclaration = new CompiledDeclaration(
            Read(zone).Resource.Address,
            DeclarationJson.Write(new { deploymentTarget = target.TechnicalName, label = zone.Label }),
            "hash",
            [],
            "domains.yaml",
            1);
        var targetReference = new GitPreviewResolvedReference(
            new ResourceAddress("DeploymentTarget", target.TechnicalName),
            target.Id);
        var zonePreview = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await ((IDeclarativeAdoptionPreviewProvider)Provider("DomainZone")).PreviewAdoptionAsync(
                new PreviewLifecycleAdoptionRequest(zone.TenantId, zoneDeclaration, zone.Id, [targetReference]),
                session,
                default)).Value);
        Assert.True(JsonElement.DeepEquals(zonePreview.Current.Projection.Identity, zonePreview.Desired.Identity));
        var declaration = new CompiledDeclaration(
            Read(binding).Resource.Address,
            DeclarationJson.Write(
                new
                {
                    zone = Read(zone).Resource.Address.Name,
                    resource = new { kind = "WebService", name = "external-workload" },
                    deploymentTarget = target.TechnicalName,
                    relativeName = binding.RelativeName,
                }),
            "hash",
            [],
            "domains.yaml",
            2);
        GitPreviewResolvedReference[] references =
        [
            targetReference, new(Read(zone).Resource.Address, zone.Id),
            new(new ResourceAddress("WebService", "external-workload"), binding.ResourceId),
        ];
        var preview = Assert.IsType<GitLifecycleAdoptionPreview>(
            (await ((IDeclarativeAdoptionPreviewProvider)Provider("HostnameBinding")).PreviewAdoptionAsync(
                new PreviewLifecycleAdoptionRequest(zone.TenantId, declaration, binding.Id, references),
                session,
                default)).Value);
        Assert.Equal(binding.Id, preview.Current.ResourceId);
        Assert.True(JsonElement.DeepEquals(preview.Current.Projection.Identity, preview.Desired.Identity));
        Assert.Contains(
            new DeclarationDocumentVersion($"DomainZone/{zone.Id}/ReferenceFence", zone.ReferenceFence),
            preview.Desired.Dependencies!.Documents);
        Assert.Contains(
            new DeclarationDocumentVersion($"DeploymentTarget/{target.Id}", target.Revision),
            preview.Desired.Dependencies.Documents);
        Assert.Contains(
            new DeclarationDocumentVersion("Workload/root", workload.Version),
            preview.Desired.Dependencies.Documents);
        Assert.Equal(zone.ReferenceFence, (await Zones.Find(_ => true).SingleAsync()).ReferenceFence);
        Assert.Equal(binding.ReferenceFence, (await Bindings.Find(_ => true).SingleAsync()).ReferenceFence);
        Assert.IsType<GitLifecycleResourceAbsent>(
            (await Provider("HostnameBinding")
                .ReadLifecycleAsync(
                    new ReadLifecycleResourceRequest(ObjectId.GenerateNewId().ToString(), Read(binding).Resource),
                    session,
                    default)).Value);
        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task New_custom_binding_invalidates_an_empty_reviewed_reverse_set()
    {
        var zone = Zone();
        await Zones.InsertOneAsync(zone);
        var provider = Services.GetServices<IDeclarativeLifecycleDependencyProvider>()
            .Single(item => item.DependencyOwner == "Domains");
        using var session = await Fixture.Client.StartSessionAsync();
        Assert.Empty(
            await provider.ReadDependenciesAsync(
                new ReadLifecycleDependenciesRequest(zone.TenantId, Read(zone).Resource),
                session,
                default));
        await Bindings.InsertOneAsync(Binding(zone));
        session.StartTransaction();
        Assert.IsType<DeclarationPreconditionsChanged>(
            (await provider.FenceDependenciesAsync(
                new FenceLifecycleDependenciesRequest(zone.TenantId, Read(zone).Resource, []),
                session,
                default)).Value);
        await session.AbortTransactionAsync();
    }

    private TenantDomainZone Zone() => TenantDomainZoneFactory.CreateManagedReservation(
        Data.TenantId,
        ObjectId.GenerateNewId().ToString(),
        "company",
        DnsName.Create("apps.example.test"),
        "company-zone",
        new TenantDomainZoneFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));

    private static HostnameBinding Binding(TenantDomainZone zone) => HostnameBindingFactory.CreateCustom(
        zone.TenantId,
        zone.Id,
        zone.DeploymentTargetId,
        ResourceTypeId.Create("pole/web-service"),
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        "api",
        DnsName.Create(zone.Domain),
        "api-hostname",
        new HostnameBindingFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));

    private static ReadLifecycleResourceRequest Read(TenantDomainZone zone) => new(
        zone.TenantId,
        new GitLifecycleResourceReference(
            new ResourceAddress("DomainZone", ((NamedDomainIdentity)zone.Identity).TechnicalName),
            zone.Id));

    private static ReadLifecycleResourceRequest Read(HostnameBinding binding) => new(
        binding.TenantId,
        new GitLifecycleResourceReference(
            new ResourceAddress("HostnameBinding", ((NamedDomainIdentity)binding.Identity).TechnicalName),
            binding.Id));

    private sealed class RemovalCapability(
        IMongoCollection<HostnameBinding> bindings
    ) : IHostnameBindingRemovalCapability
    {
        public string DeploymentId { get; set; } = ObjectId.GenerateNewId().ToString();
        public bool FailRemoval { get; set; }
        public bool SawFence { get; private set; }
        public long Version { get; private set; } = 1;
        public bool SawAbsentBindingAtRemoval { get; private set; }
        public HostnameAttachmentProvenance? Provenance { get; private set; }
        public ReadHostnameAttachmentRemovalOutcome? OwnerState { get; set; }
        public string Kind => "WebService";
        public ResourceTypeId ResourceType => ResourceTypeId.Create("pole/web-service");

        public Task<ReadHostnameAttachmentOutcome> ReadAsync(
            ReadHostnameAttachmentRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken) => Task.FromResult<ReadHostnameAttachmentOutcome>(Snapshot(request));

        public Task<ReadHostnameAttachmentRemovalOutcome> ReadRemovalAsync(
            ReadHostnameAttachmentRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken) => Task.FromResult(
            OwnerState ?? new ReadHostnameAttachmentRemovalOutcome(Snapshot(request)));

        public Task<FenceHostnameAttachmentOutcome> FenceAsync(
            HostnameAttachmentSnapshot snapshot,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
        {
            SawFence = true;
            Version++;
            return Task.FromResult<FenceHostnameAttachmentOutcome>(new HostnameAttachmentFenced());
        }

        public Task<ApplyHostnameAttachmentOutcome> ApplyAsync(
            ApplyHostnameAttachmentRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<ApplyHostnameAttachmentOutcome> RemoveAsync(
            RemoveHostnameAttachmentRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken)
        {
            if (FailRemoval)
                throw new InvalidOperationException("Injected workload attachment write failure");
            Assert.Equal(Version, Assert.Single(request.Snapshot.DocumentVersions).Version);
            Provenance = request.Provenance;
            SawAbsentBindingAtRemoval = !await bindings.Find(session, item => item.Id == request.BindingId)
                .AnyAsync(cancellationToken);
            return new HostnameAttachmentApplied(true);
        }

        private HostnameAttachmentSnapshot Snapshot(ReadHostnameAttachmentRequest request) => new(
            request.TenantId,
            request.ResourceId,
            ResourceType,
            request.DeploymentTargetId,
            DeploymentId,
            request.Hostname,
            [request.Hostname],
            [new HostnameAttachmentDocumentVersion("Workload/root", Version)],
            HostnameAttachmentAvailability.Active);
    }
}
