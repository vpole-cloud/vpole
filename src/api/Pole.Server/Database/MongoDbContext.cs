using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Domain;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Domain;
using PostgresCluster = Pole.Features.Postgres.Domain.PostgresCluster;

namespace Pole.Server.Database;

public class MongoDbContext(
    IMongoClient client,
    IMongoDatabase database
    ) : 
    ISearchDbContext,
    ITenantDbContext,
    IUserDbContext
{
    public IMongoDatabase Database { get; } = database;
    public IMongoClient Client { get; } = client;
    public IMongoCollection<SearchDocument> SearchDocuments { get; } = database.GetCollection<SearchDocument>("SearchDocuments");
    // Transitional host/test convenience accessors. ResourceGroups.Application consumes only
    // feature-owned capabilities and does not depend on this concrete context.
    public IMongoCollection<ResourceGroup> ResourceGroups { get; } = database.GetCollection<ResourceGroup>("ResourceGroups");
    public IMongoCollection<IsolationScope> IsolationScopes { get; } = database.GetCollection<IsolationScope>("IsolationScopes");
    public IMongoCollection<RuntimeCell> RuntimeCells { get; } = database.GetCollection<RuntimeCell>("RuntimeCells");
    public IMongoCollection<ResourceDeployment> ResourceDeployments { get; } = database.GetCollection<ResourceDeployment>("ResourceDeployments");
    public IMongoCollection<PostgresCluster>  Postgres { get; } = database.GetCollection<PostgresCluster>("Postgres");
    public IMongoCollection<StorageProfile> StorageProfiles { get; } = database.GetCollection<StorageProfile>("StorageProfiles");
    public IMongoCollection<OutboxMessage> OutboxMessages { get; } = database.GetCollection<OutboxMessage>("OutboxMessages");
    public IMongoCollection<Tenant> Tenants { get; } = database.GetCollection<Tenant>("Tenants");
    public IMongoCollection<PoleUser> Users { get; } = database.GetCollection<PoleUser>("Users");
    public IMongoCollection<GitProviderConnection> GitProviderConnections { get; } = database.GetCollection<GitProviderConnection>("GitProviderConnections");
    public IMongoCollection<GitHubSetupAttempt> GitHubSetupAttempts { get; } = database.GetCollection<GitHubSetupAttempt>("GitHubSetupAttempts");
    public IMongoCollection<SourceRepository> SourceRepositories { get; } = database.GetCollection<SourceRepository>("SourceRepositories");
    public IMongoCollection<RegistryProject> RegistryProjects { get; } = database.GetCollection<RegistryProject>("RegistryProjects");
    public IMongoCollection<RegistryCredential> RegistryCredentials { get; } = database.GetCollection<RegistryCredential>("RegistryCredentials");
    public IMongoCollection<DeploymentTarget> DeploymentTargets { get; } = database.GetCollection<DeploymentTarget>("DeploymentTargets");
    public IMongoCollection<DeploymentTargetReference> DeploymentTargetReferences { get; } = database.GetCollection<DeploymentTargetReference>("DeploymentTargetReferences");
    public IMongoCollection<DeploymentTargetSettings> DeploymentTargetSettings { get; } = database.GetCollection<DeploymentTargetSettings>("DeploymentTargetSettings");
    public IMongoCollection<SourceBuildConfiguration> SourceBuildConfigurations { get; } = database.GetCollection<SourceBuildConfiguration>("SourceBuildConfigurations");
    public IMongoCollection<SourceBuildJob> SourceBuildJobs { get; } = database.GetCollection<SourceBuildJob>("SourceBuildJobs");
    public IMongoCollection<FunctionApp> FunctionApps { get; } = database.GetCollection<FunctionApp>("FunctionApps");
    public IMongoCollection<FunctionRevision> FunctionRevisions { get; } = database.GetCollection<FunctionRevision>("FunctionRevisions");
    public IMongoCollection<FunctionRouteBinding> FunctionRouteBindings { get; } = database.GetCollection<FunctionRouteBinding>("FunctionRouteBindings");
    public IMongoCollection<FunctionTimerState> FunctionTimerStates { get; } = database.GetCollection<FunctionTimerState>("FunctionTimerStates");
    public IMongoCollection<FunctionInvocation> FunctionInvocations { get; } = database.GetCollection<FunctionInvocation>("FunctionInvocations");
    public IMongoCollection<FunctionMeterEvent> FunctionMeterEvents { get; } = database.GetCollection<FunctionMeterEvent>("FunctionMeterEvents");
    public IMongoCollection<FunctionUsageBucket> FunctionUsageBuckets { get; } = database.GetCollection<FunctionUsageBucket>("FunctionUsageBuckets");
}
