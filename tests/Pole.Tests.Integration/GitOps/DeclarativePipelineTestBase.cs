using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application;
using Pole.Features.ComputeProfiles.Application.Catalog;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Migrations;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Endpoints;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Migrations;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.SourceDelivery;
using Pole.Features.WebServices.Application.Tokens;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Migrations;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase(
    MongoReplicaSetFixture fixture,
    bool isolatedDatabase = false) : MediatorIntegrationTestBase(fixture, isolatedDatabase)
{
    private readonly bool _isolatedDatabase = isolatedDatabase;

    // Isolated scenarios retain the original database-wide guards; shared scenarios own one tenant.
    private protected FilterDefinition<T> ScenarioFilter<T>(Expression<Func<T, string?>> tenantId) =>
        _isolatedDatabase ? Builders<T>.Filter.Empty : Builders<T>.Filter.Eq(tenantId, Data.TenantId);

    private protected readonly string source = ObjectId.GenerateNewId().ToString();

    private protected readonly TestRepositorySnapshots repository = new();

    private protected bool rejectNextWebServiceRevision;

    private protected RepositoryPipelineDriver Pipeline => new(Services, Fixture, Data, repository);

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
        ConfigureSourceDeliveryServices(services);
        var originalPersistence = services.Last(item =>
            item.ServiceType == typeof(PersistUpdatedWebServiceOutcome)).ImplementationFactory!;
        services.AddScoped<PersistUpdatedWebServiceOutcome>(provider =>
        {
            var persist = (PersistUpdatedWebServiceOutcome)originalPersistence(provider);
            return async (session, current, next, changed, ct, mutation) =>
            {
                if (rejectNextWebServiceRevision)
                {
                    rejectNextWebServiceRevision = false;
                    return new WebServiceUpdateConflict(current.Id);
                }

                return await persist(session, current, next, changed, ct, mutation);
            };
        });
        foreach (var descriptor in services.Where(item =>
                     (item.ServiceType == typeof(Pole.Features.DeploymentTargets.Contracts
                          .FenceDeploymentTargetWorkloadConfigurations) ||
                      item.ServiceType == typeof(Pole.Features.DeploymentTargets.Contracts
                          .ListEligiblePublicIngressDeploymentTargetsForNewWorkload)) &&
                     item.Lifetime == ServiceLifetime.Singleton).ToArray())
            services.Remove(descriptor);
    }

    private protected IMongoCollection<WebService> WebServices => Fixture.Database.GetCollection<WebService>("WebServices");

    private protected IMongoCollection<Secret> Secrets => Fixture.Database.GetCollection<Secret>("Secrets");

    private protected IMongoCollection<ManagedDeclaration> Inventory =>
        Fixture.Database.GetCollection<ManagedDeclaration>("ManagedDeclarations");

    private protected static string Id() => ObjectId.GenerateNewId().ToString();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using (Timings.Measure("gitops.seed.catalog"))
            await Fixture.PrepareNamedCatalogAsync();
    }

    internal static ManifestFile[] Snapshot(string tag = "1", string[]? keys = null, long profileRevision = 1)
    {
        static string Document(string kind, string name, object spec) => JsonSerializer.Serialize(
            new { apiVersion = "pole.io/v1alpha1", kind, metadata = new { name }, spec },
            DeclarationJson.Options);

        // Deliberately unordered multi-document input, including the binding/service preparation cycle.
        return
        [
            new("demo.yaml",
                string.Join("\n---\n",
                    new[]
                    {
                        Document("SecretBinding",
                            "demo-api-credentials-access",
                            new
                            {
                                secret = "demo-api-credentials",
                                consumer = new { kind = "WebService", name = "demo-api" }
                            }),
                        Document("WebService",
                            "demo-api",
                            new
                            {
                                resourceGroup = "demo", image = new { server = "docker.io", name = "nginx", tag },
                                placement = new
                                {
                                    deploymentTarget = NamedCatalogTestData.TargetName,
                                    computeProfile = new
                                        { name = NamedCatalogTestData.ProfileName, revision = profileRevision }
                                },
                                ports = new[]
                                    { new { servicePort = 80, targetPort = "8080", protocol = "TCP", name = "http" } },
                                probes = new
                                {
                                    startup = TestData.ValidProbe(), readiness = TestData.ValidProbe(),
                                    liveness = TestData.ValidProbe()
                                },
                                secretEnvironment = new Dictionary<string, object>
                                {
                                    ["API_TOKEN"] = new
                                        { secret = "demo-api-credentials", key = keys?.Last() ?? "API_TOKEN" }
                                }
                            }),
                        Document("Secret",
                            "demo-api-credentials",
                            new { resourceGroup = "demo", keys = keys ?? ["API_TOKEN"] }),
                        Document("ResourceGroup", "demo", new { description = "Git acceptance" })
                    }))
        ];
    }

    private protected Task<RepositoryPipelineResult> Apply(
        ManifestFile[]? files = null,
        string? sourceId = null,
        string commit = "snapshot") =>
        Pipeline.SubmitAsync(new(Data.TenantId, sourceId ?? source), files ?? Snapshot(), commit);

    private protected async Task AssertNoAcceptedSnapshotAsync()
    {
        var runs = await Fixture.Database.GetCollection<GitReconciliationRun>("GitReconciliationRuns")
            .Find(item => item.Stamp.Source.SourceId == source).ToListAsync();
        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.IsNotType<GitInputAccepted>(run.Input));
    }

    private protected async Task Supply(string value = "")
    {
        var secret = await Secrets.Find(ScenarioFilter<Secret>(item => item.TenantId)).SingleAsync();
        var result = await Services.GetRequiredService<PersistRotatedSecret>()(secret.TenantId,
            secret.Id,
            secret.CurrentVersion,
            secret.KeyNames.ToDictionary(key => key, _ => value),
            default);
        Assert.IsType<Secret>(result.Value);
    }

    private protected Task<long> Releases() => Fixture.Database.GetCollection<WebServiceRelease>("WebServiceReleases")
        .CountDocumentsAsync(ScenarioFilter<WebServiceRelease>(item => item.TenantId));

    private protected Task<WebService> Service() => WebServices.Find(ScenarioFilter<WebService>(item => item.TenantId)).SingleAsync();

    private protected static ManifestFile[] SnapshotWithAliases(string alias,
        bool missingReplacementValue = false,
        bool explicitIdentity = false) =>
        Snapshot(keys: missingReplacementValue ? ["API_TOKEN", "SECOND_TOKEN"] : null).Select(file => file with
        {
            Content = string.Join("\n---\n",
                file.Content.Split("\n---\n").Select(document =>
                {
                    var node = JsonNode.Parse(document)!;
                    if (node["kind"]!.GetValue<string>() == "SecretBinding" &&
                        (alias != "API_TOKEN" || explicitIdentity))
                    {
                        var mappings = new JsonObject { ["API_TOKEN"] = alias };
                        if (missingReplacementValue)
                            mappings["SECOND_TOKEN"] = "SECOND_TOKEN";
                        node["spec"]!["keyMappings"] = mappings;
                    }

                    return node.ToJsonString();
                }))
        }).ToArray();

    private protected sealed class PauseAfterBlockedConfigure(IGitOpsMutations inner) : DelegatingGitOpsMutations(inner)
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<GitOperationResult> ExecuteAsync(GitOperation operation,
            Func<IClientSessionHandle, CancellationToken, Task<GitOperationResult>> mutation, CancellationToken ct)
        {
            var result = await base.ExecuteAsync(operation, mutation, ct);
            if (operation.Phase == DeclarationPhase.Configure && result.Failure is WebServiceSecretReferenceRejected
                { Error.Reason: ConsumerSecretReferenceError.SecretValuesMissing })
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }

            return result;
        }
    }

    private protected sealed class InjectedOutboxFailure : Exception;
}

public abstract class IsolatedDeclarativePipelineTestBase(MongoReplicaSetFixture fixture)
    : DeclarativePipelineTestBase(fixture, true), IIsolatedIntegrationTest;
