using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.ResourceGroups.Testing;

public sealed record TestResourceGroup(
    string Id,
    string DefaultIsolationScopeId
);

public sealed class ResourceGroupTestData(
    IMongoDatabase database
)
{
    public async Task<TestResourceGroup> CreateAsync(string tenantId, string name = "Test group")
    {
        var group = ResourceGroupFactory.Create(tenantId, name, "", ResourceGroupFactory.DefaultCreateOptions);
        return await SeedAsync(group);
    }

    public async Task<TestResourceGroup> SeedAsync(ResourceGroup group)
    {
        var scope = IsolationScopeFactory.CreateDefault(
            group.TenantId,
            group.Id,
            new IsolationScopeFactory.CreateOptions(group.DefaultIsolationScopeId, group.CreatedAtUtc));
        using var session = await database.Client.StartSessionAsync();
        await session.WithTransactionAsync(async (transaction, ct) =>
        {
            await database.GetCollection<ResourceGroup>("ResourceGroups")
                .InsertOneAsync(transaction, group, cancellationToken: ct);
            await database.GetCollection<IsolationScope>("IsolationScopes")
                .InsertOneAsync(transaction, scope, cancellationToken: ct);
            return true;
        });
        return new TestResourceGroup(group.Id, group.DefaultIsolationScopeId);
    }

    public async Task AssertRegisteredAsync(
        string tenantId,
        string groupId,
        ResourceTypeId resourceType,
        string resourceId,
        string displayName)
    {
        var registration = await database.GetCollection<ResourceRegistration>("ResourceRegistrations")
            .Find(item => item.TenantId == tenantId && item.ResourceId == resourceId &&
                item.ResourceTypeId == resourceType.Value)
            .SingleAsync();
        Assert.Equal(groupId, registration.ResourceGroupId);
        Assert.Equal(displayName, registration.DisplayName);
    }

    public async Task AssertNoRegistrationsAsync(string tenantId) => Assert.Equal(
        0L,
        await database.GetCollection<ResourceRegistration>("ResourceRegistrations")
            .CountDocumentsAsync(item => item.TenantId == tenantId));

    public static void RegisterCreationContracts(IServiceCollection services)
    {
        services.AddResourceGroupsFeature(new ConfigurationBuilder().Build());
        services.TryAddSingleton<IGitLifecycleCleanupProofRecorder, UnexpectedCleanup>();
    }

    private sealed class UnexpectedCleanup : IGitLifecycleCleanupProofRecorder
    {
        public Task RecordAsync(
            RecordGitLifecycleCleanupRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Resource creation unexpectedly requested GitOps cleanup.");
    }
}
