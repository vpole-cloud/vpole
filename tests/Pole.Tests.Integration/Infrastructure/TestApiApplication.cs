using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using Pole.Encryption;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Migrations;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Domains.Application;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Migrations;
using Pole.Features.ContainerRegistries.Application.Common;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application.Migrations;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Contracts.HttpMapping;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Outbox.Application.Migrations;
using Pole.Features.Quotas.Application;
using Pole.Features.Quotas.Application.Common;
using Pole.Features.Quotas.Contracts.HttpMapping;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Migrations;
using Pole.Features.ResourceDeployments.Contracts.HttpMapping;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Migrations;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Application.Services;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Migrations;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Migrations;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Middleware;
using Pole.Features.Tenants.Application.Common;
using Pole.Observability;
using Pole.Server.Database;
using Pole.Server.Extensions;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestApiApplication : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _encryptionKeyDirectory;

    private static readonly Assembly[] Assemblies =
    [
        typeof(WebServiceEndpoints).Assembly,
        typeof(ComputeProfileApplicationRegistration).Assembly,
        typeof(FunctionAppFeature).Assembly,
        typeof(FunctionRuntimeFeature).Assembly,
        typeof(PostgresClusterEndpoints).Assembly,
        typeof(QuotaEndpoints).Assembly,
        typeof(ResourceGroupEndpoints).Assembly,
        typeof(RedisServiceEndpoints).Assembly,
        typeof(ResourceDeploymentFeature).Assembly,
        typeof(DomainZoneEndpoints).Assembly,
        typeof(GitProviderEndpoints).Assembly,
        typeof(ContainerRegistryFeature).Assembly,
        typeof(DeploymentTargetFeature).Assembly,
        typeof(SourceBuildFeature).Assembly,
        typeof(SearchEndpoints).Assembly,
        typeof(StorageProfileEndpoints).Assembly
    ];

    private TestApiApplication(
        WebApplication app,
        FakeGitHubAppClient gitHubClient,
        TestDeploymentTargetLookup deploymentTargetLookup,
        string encryptionKeyDirectory)
    {
        _app = app;
        _encryptionKeyDirectory = encryptionKeyDirectory;
        GitHubClient = gitHubClient;
        DeploymentTargetLookup = deploymentTargetLookup;
        Client = app.GetTestClient();
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TestData.TenantId);
    }

    public HttpClient Client { get; }
    public FakeGitHubAppClient GitHubClient { get; }
    private TestDeploymentTargetLookup DeploymentTargetLookup { get; }

    public void RejectNewDeploymentTargetWorkloads() =>
        DeploymentTargetLookup.RejectNewWorkloads = true;

    public void AddEligibleDeploymentTarget(
        string deploymentTargetId,
        string publicBaseDomain) =>
        DeploymentTargetLookup.AddEligibleTarget(
            deploymentTargetId,
            publicBaseDomain);

    public void RejectDeploymentTargetReferenceRemoval() =>
        DeploymentTargetLookup.RejectReferenceRemoval = true;

    public static async Task<TestApiApplication> StartAsync(MongoReplicaSetFixture fixture)
    {
        var encryptionKeyDirectory = Path.Combine(
            Path.GetTempPath(),
            "pole-api-integration-encryption",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(encryptionKeyDirectory);
        var encryptionKeyPath = Path.Combine(encryptionKeyDirectory, "api-v1.key");
        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            await File.WriteAllTextAsync(encryptionKeyPath, Convert.ToBase64String(encryptionKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EnvelopeEncryption:ActiveProviderId"] = LocalKeyFileKeyEncryptionProvider.StableProviderId,
            ["EnvelopeEncryption:LocalKeyFile:ActiveKeyId"] = "api-integration",
            ["EnvelopeEncryption:LocalKeyFile:ActiveKeyVersion"] = "1",
            ["EnvelopeEncryption:LocalKeyFile:Keys:0:Id"] = "api-integration",
            ["EnvelopeEncryption:LocalKeyFile:Keys:0:Version"] = "1",
            ["EnvelopeEncryption:LocalKeyFile:Keys:0:Path"] = encryptionKeyPath,
            ["DeploymentTargets:Bootstrap:Enabled"] = "false",
            ["kubernetes:NamespaceNamePrefix"] = "test"
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddSingleton(fixture.Database);
        builder.Services.AddSingleton<MongoDbContext>(_ => fixture.DbContext);
        var storageDiscovery = new TestStorageDiscoveryService("standard", "fast", "bulk");
        builder.Services.AddSingleton(storageDiscovery);
        builder.Services.AddSingleton<DiscoverKubernetesStorageClasses>(
            storageDiscovery.GetStorageClassesAsync);
        builder.Services.AddSingleton<DiscoverKubernetesStorageClassNames>(
            storageDiscovery.GetStorageClassNamesAsync);
        builder.Services.AddSingleton<DiscoverKubernetesPersistentVolumes>(
            storageDiscovery.GetPersistentVolumesAsync);
        builder.Services.AddSingleton<DiscoverKubernetesPersistentVolumeClaims>(
            storageDiscovery.GetPersistentVolumeClaimsAsync);
        builder.Services.AddMongoDbContextInterfaces();
        builder.Services.AddFunctionAppsFeature();
        builder.Services.AddResourceDeploymentsFeature(builder.Configuration);
        builder.Services.AddResourceGroupsFeature(builder.Configuration);
        builder.Services.AddHttpProblemMappings(
            ResourceDeploymentContractHttpMappings.All);
        builder.Services.AddHttpProblemMappings(
            DeploymentTargetContractHttpMappings.All);
        builder.Services.AddTenantContractCapabilities();
        builder.Services.AddDomainsFeature();
        builder.Services.AddHttpProblemMappings(QuotaContractHttpMappings.All);
        builder.Services.AddComputeProfilesFeature();
        builder.Services.AddStorageProfilesFeature();
        builder.Services.AddRedisServicesFeature();
        builder.Services.AddPostgresFeature();
        builder.Services.AddWebServicesFeature();
        builder.Services.AddSingleton(new PostgresSettings
        {
            DefaultDeploymentMode = Pole.Features.Postgres.Domain.PostgresDeploymentMode.Operator,
            SupportedVersions = new PostgresSupportedVersionsSettings { From = 16, To = 17 }
        });
        builder.Services.AddSingleton(new QuotaSettings());
        builder.Services.AddQuotasFeature(
            typeof(RedisQuotaProfile).Assembly,
            typeof(FunctionAppQuotaProfile).Assembly,
            typeof(WebServiceQuotaProfile).Assembly,
            typeof(PostgresQuotaProfile).Assembly);
        builder.Services.AddMigrations(Assemblies);
        builder.Services.AddSearchFeature();
        builder.Services.AddGitProvidersFeature();
        builder.Services.AddEnvelopeEncryption(builder.Configuration);
        builder.Services.AddDeploymentTargetsFeature(builder.Configuration);
        builder.Services.RemoveAll<ProbeDeploymentTargetConnection>();
        var deploymentTargetProbe = new TestDeploymentTargetProbe();
        builder.Services.AddSingleton(deploymentTargetProbe);
        builder.Services.AddSingleton<ProbeDeploymentTargetConnection>(
            deploymentTargetProbe.InvokeAsync);
        builder.Services.RemoveAll<LookupDeploymentTarget>();
        builder.Services.RemoveAll<LookupDefaultDeploymentTarget>();
        builder.Services.RemoveAll<ListEligibleDeploymentTargetsForNewWorkload>();
        builder.Services.RemoveAll<AdmitDefaultDeploymentTargetForNewWorkload>();
        builder.Services.RemoveAll<AdmitDeploymentTargetForWorkload>();
        builder.Services.RemoveAll<AdmitDeploymentTargetForNewWorkload>();
        builder.Services.RemoveAll<AdmitDeploymentTargetPublicIngressForNewWorkload>();
        builder.Services.RemoveAll<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>();
        builder.Services.RemoveAll<AdmitDeploymentTargetPublicIngressForDomainReservation>();
        builder.Services.RemoveAll<ApplyDeploymentTargetReferenceChanges>();
        builder.Services.RemoveAll<FenceDeploymentTargetWorkloadConfigurations>();
        var deploymentTargetLookup = new TestDeploymentTargetLookup();
        builder.Services.AddSingleton<LookupDeploymentTarget>(
            deploymentTargetLookup.LookupAsync);
        builder.Services.AddSingleton<LookupDefaultDeploymentTarget>(
            deploymentTargetLookup.LookupDefaultAsync);
        builder.Services.AddSingleton<ListEligibleDeploymentTargetsForNewWorkload>(
            deploymentTargetLookup.ListEligibleAsync);
        builder.Services.AddSingleton<AdmitDefaultDeploymentTargetForNewWorkload>(
            deploymentTargetLookup.AdmitDefaultForNewWorkloadAsync);
        builder.Services.AddSingleton<AdmitDeploymentTargetForWorkload>(
            deploymentTargetLookup.AdmitForWorkloadAsync);
        builder.Services.AddSingleton<AdmitDeploymentTargetForNewWorkload>(
            deploymentTargetLookup.AdmitForWorkloadPlacementAsync);
        builder.Services.AddSingleton<AdmitDeploymentTargetPublicIngressForNewWorkload>(
            deploymentTargetLookup.AdmitForNewWorkloadAsync);
        builder.Services.AddSingleton<ListEligiblePublicIngressDeploymentTargetsForNewWorkload>(
            deploymentTargetLookup.ListEligiblePublicIngressForNewWorkloadAsync);
        builder.Services.AddSingleton<
            AdmitDeploymentTargetPublicIngressForDomainReservation>(
            deploymentTargetLookup.AdmitForDomainReservationAsync);
        builder.Services.AddSingleton<ApplyDeploymentTargetReferenceChanges>(
            deploymentTargetLookup.ApplyChangesOutcomeAsync);
        builder.Services.AddSingleton<FenceDeploymentTargetWorkloadConfigurations>(
            deploymentTargetLookup.FenceManyOutcomeAsync);
        builder.Services.AddSingleton(new HarborOptions());
        builder.Services.AddContainerRegistriesFeature();
        builder.Services.AddSingleton<ISourceBuildJobDispatcher, TestSourceBuildJobDispatcher>();
        builder.Services.AddSingleton(new SourceBuildExecutionOptions());
        builder.Services.AddSingleton(new BuildKitOptions());
        builder.Services.AddSourceBuildsFeature();
        var gitHubClient = new FakeGitHubAppClient();
        builder.Services.RemoveAll<IGitHubAppClient>();
        builder.Services.AddSingleton<IGitHubAppClient>(gitHubClient);
        builder.Services.AddSingleton(new GitHubAppOptions
        {
            Enabled = true,
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            AppSlug = "pole-test",
            PrivateKeyPath = "not-used-by-fake.pem",
            PublicBaseUrl = "https://pole.test",
            FrontendBaseUrl = "http://localhost:5173"
        });
        builder.Services.AddValidatorsFromAssemblies(Assemblies);
        builder.Services.AddPoleMediator(Assemblies);
        builder.Services
            .AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(Policy.Admin, policy =>
                policy.RequireClaim(Claims.Roles, Roles.Admin));
        });
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("frontend-dev", policy =>
                policy.WithOrigins("http://localhost:5173")
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .WithExposedHeaders(TraceHeaders.CorrelationId, TraceHeaders.TraceId));
        });

        var app = builder.Build();
        await new DeploymentTargetMigration().Up(fixture.Database);
        await new DeploymentTargetReferenceMigration().Up(fixture.Database);
        await new ResourceGroupPlacementMigration().Up(fixture.Database);
        await new ResourceDeploymentMigration().Up(fixture.Database);
        await new ResourceDeploymentSchemaValidationMigration().Up(fixture.Database);
        await new WebServiceReleaseSchemaValidationMigration().Up(fixture.Database);
        await new TenantDomainZoneSchemaValidationMigration().Up(fixture.Database);
        await new HostnameBindingSchemaValidationMigration().Up(fixture.Database);
        await new DomainIndexesMigration().Up(fixture.Database);
        await new ComputeProfileSchemaValidationMigration().Up(fixture.Database);
        await new ComputeProfileIndexesMigration().Up(fixture.Database);
        await new ResourceDeploymentComputeProfileIndexMigration().Up(fixture.Database);
        await new ResourceDeploymentRecoveryIndexMigration().Up(fixture.Database);
        await ((IMigration)new PostgresCredentialMigration()).Up(fixture.Database);
        await new StorageProfileMigration().Up(fixture.Database);
        await new OutboxMessageMigration().Up(fixture.Database);
        await fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(TestData.SeedComputeProfile());
        app.UseTraceContext();
        app.UseCors("frontend-dev");
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<TenantContextMiddleware>();
        app.MapGitHubCallbackEndpoints();
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization();
        api
            .MapResourceGroupEndpoints()
            .MapContainerRegistryEndpoints()
            .MapDeploymentTargetEndpoints()
            .MapDeploymentTargetAvailabilityEndpoints()
            .MapDeploymentTargetEncryptionEndpoints()
            .MapWebServiceEndpoints()
            .MapRedisServiceEndpoints()
            .MapResourceDeploymentEndpoints()
            .MapFunctionAppEndpoints()
            .MapPostgresClusterEndpoints()
            .MapQuotaEndpoints()
            .MapSearchEndpoints()
            .MapDomainZoneEndpoints()
            .MapStorageProfileEndpoints()
            .MapGitProviderEndpoints()
            .MapSourceBuildEndpoints();

        api.MapGroup("/platform/compute-profiles")
            .WithTags("PlatformComputeProfiles")
            .RequireAuthorization(Policy.Admin)
            .MapGetComputeProfilesEndpoint()
            .MapGetComputeProfileEndpoint()
            .MapCreateComputeProfileEndpoint()
            .MapUpdateComputeProfileEndpoint()
            .MapDeleteComputeProfileEndpoint();

        api.MapGroup("/compute-profiles")
            .WithTags("ComputeProfiles")
            .MapGetAvailableComputeProfilesEndpoint();

        await app.StartAsync();

        return new TestApiApplication(
            app,
            gitHubClient,
            deploymentTargetLookup,
            encryptionKeyDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(_encryptionKeyDirectory, recursive: true);
    }

}
