using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionResourceDeploymentAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_WithoutLogicalApp_ClosesGatewayPolicyBeforeReturning(bool policyFailure)
    {
        var requests = new List<ResourceIngressPolicyRequest>();
        IResourceDeploymentAdapter subject = new FunctionResourceDeploymentAdapter(
            (_, _, _) => Task.FromResult<FunctionApp?>(null),
            (_, _) => throw new Xunit.Sdk.XunitException("No revisions should be loaded."),
            null!,
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<ReconcileResourceIngressPolicyOutcome>(
                    policyFailure
                        ? new ResourceIngressPolicyUnavailable()
                        : new ResourceIngressPolicyApplied());
            });

        var result = await subject.DeleteAsync(Context());

        var request = Assert.Single(requests);
        Assert.Equal("function-gateway", request.Purpose);
        Assert.Empty(request.AllowedTcpPorts);
        Assert.Equal(DeploymentId, request.ResourceDeploymentId);
        if (policyFailure)
        {
            var failed = Assert.IsType<ResourceDeploymentAdapterResult.Failed>(result);
            Assert.Equal("FunctionGatewayIngressCleanupUnavailable", failed.FailureCode);
            Assert.True(failed.Retryable);
        }
        else
        {
            Assert.IsType<ResourceDeploymentAdapterResult.Succeeded>(result);
        }
    }

    private static ResourceDeploymentReconciliationContext Context() => new(
        new ResourceDeploymentReconciliationSnapshot(
            DeploymentId,
            TenantId,
            FunctionResourceDeployment.ResourceType,
            ResourceId,
            GroupId,
            ScopeId,
            RuntimeCellId,
            ResourceRevision: 1,
            FixedReplicas: 1,
            DesiredGeneration: 2,
            UpdatedAtUtc: DateTime.UtcNow,
            new ResourceDeploymentResolvedPlacementSnapshot(
                TargetId,
                TargetWorkloadConfigurationRevision: 1,
                "pole-functions",
                Ingress: null,
                Storage: null,
                ResourceDeploymentTestData.ContractCompute(TargetId))));

    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string GroupId = "507f1f77bcf86cd799439013";
    private const string ScopeId = "507f1f77bcf86cd799439014";
    private const string RuntimeCellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
}
