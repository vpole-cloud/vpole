using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Application.Jobs;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Application.Placements;
using Pole.Features.ResourceDeployments.Application.Planning;
using Pole.Features.ResourceDeployments.Application.Queries;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentCreationContractTests(
    MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439013";
    private const string ResourceGroupId = "507f1f77bcf86cd799439014";
    private const string DeploymentId = "507f1f77bcf86cd799439018";
    private const string TargetId = "507f1f77bcf86cd799439017";
    private static readonly ResourceTypeId TestResourceType =
        ResourceTypeId.Create("pole/test-resource");

    [Fact]
    public async Task CreateSet_RejectsEmptyAndDuplicateDeploymentSets()
    {
        var create = Services.GetRequiredService<CreateResourceDeploymentSet>();
        var deployment = CreateRequest();

        await AssertStructuralRequestRejectedAsync(
            create,
            new CreateResourceDeploymentSetRequest([]));
        await AssertStructuralRequestRejectedAsync(
            create,
            new CreateResourceDeploymentSetRequest([null!]));
        await AssertStructuralRequestRejectedAsync(
            create,
            new CreateResourceDeploymentSetRequest([deployment, deployment]));
    }

    [Fact]
    public void FeatureRegistration_ResolvesCanonicalCapabilities()
    {
        _ = Services.GetRequiredService<CreateResourceDeployment>();
        _ = Services.GetRequiredService<CreateResourceDeploymentSet>();
        _ = Services.GetRequiredService<RequestResourceDeploymentApply>();
        _ = Services.GetRequiredService<RequestResourceDeploymentDeletion>();
        _ = Services.GetRequiredService<RequestExactResourceDeploymentApply>();
        _ = Services.GetRequiredService<RequestExactResourceDeploymentDeletion>();
        _ = Services.GetRequiredService<RequestResourceDeploymentSetApply>();
        _ = Services.GetRequiredService<RequestResourceDeploymentSetDeletion>();
        _ = Services.GetRequiredService<AdvanceResourceDeploymentRevision>();
        _ = Services.GetRequiredService<RetryExactResourceDeployment>();
        _ = Services.GetRequiredService<LoadResourceDeployment>();
        _ = Services.GetRequiredService<LoadResourceDeployments>();
        _ = Services.GetRequiredService<LoadResourceDeploymentSet>();
        _ = Services.GetRequiredService<LoadExactResourceDeployment>();
        _ = Services.GetRequiredService<LoadResourceDeploymentSets>();
        _ = Services.GetRequiredService<LoadCurrentResourceDeploymentCompute>();
        _ = Services.GetRequiredService<EnsureNoUnfinishedResourceGroupDeployments>();
        _ = Services.GetRequiredService<EnsureNoUnfinishedIsolationScopeDeployments>();
        _ = Services.GetRequiredService<EnsureComputeProfileUnreferenced>();
        _ = Services.GetRequiredService<GetResourceDeploymentFinalizationReadiness>();
        _ = Services.GetRequiredService<PurgeDeletedResourceDeployments>();
        _ = Services.GetRequiredService<GetExactResourceDeploymentFinalizationReadiness>();
        _ = Services.GetRequiredService<PurgeExactDeletedResourceDeployment>();
        _ = Services.GetRequiredService<ResolveResourceDeploymentAdapter>();
        _ = Services.GetRequiredService<ClaimResourceDeployment>();
        _ = Services.GetRequiredService<PersistResourceDeploymentSuccess>();
        _ = Services.GetRequiredService<PersistResourceDeploymentFailure>();
        _ = Services.GetRequiredService<LoadResourceDeploymentRecoveryCandidates>();
        _ = Services.GetRequiredService<RecoverResourceDeployment>();
        _ = Services.GetRequiredService<WakeResourceDeploymentsForRuntimeCell>();
        _ = Services.GetRequiredService<RetryResourceDeployment>();
        _ = Services.GetRequiredService<ResourceDeploymentReconciler>();
        _ = Services.GetRequiredService<ResourceDeploymentRecoveryJob>();
    }

    [Fact]
    public async Task ReferenceCapabilities_RequireCallerOwnedTransaction()
    {
        var resourceGroupGuard = Services
            .GetRequiredService<EnsureNoUnfinishedResourceGroupDeployments>();
        var computeProfileGuard = Services
            .GetRequiredService<EnsureComputeProfileUnreferenced>();
        using var session = await DbContext.Client.StartSessionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resourceGroupGuard(
                new EnsureNoUnfinishedResourceGroupDeploymentsRequest(
                    TenantId: TenantId,
                    ResourceGroupId: ResourceGroupId),
                session: session));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            computeProfileGuard("507f1f77bcf86cd799439019", session));
    }

    private async Task AssertStructuralRequestRejectedAsync(
        CreateResourceDeploymentSet create,
        CreateResourceDeploymentSetRequest request)
    {
        using var session = await DbContext.Client.StartSessionAsync();
        session.StartTransaction();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                create(request, session));
        }
        finally
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync();
        }
    }

    private static CreateResourceDeploymentRequest CreateRequest() =>
        new(
            DeploymentId,
            TenantId,
            TestResourceType,
            ResourceId,
            ResourceGroupId,
            IsolationScopeId: null,
            new ResolvedResourceDeploymentTarget(
                TargetId,
                WorkloadConfigurationRevision: 1,
                Ingress: null),
            ResourceDeploymentTestData.ContractCompute(TargetId),
            Storage: null,
            ResourcePlacementCapability.MultiTarget);
}
