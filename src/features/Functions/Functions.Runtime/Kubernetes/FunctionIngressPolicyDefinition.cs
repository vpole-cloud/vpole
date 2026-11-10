using System.Collections.Immutable;
using Pole.Features.Functions.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.Functions.Runtime.Kubernetes;

internal static class FunctionIngressPolicyDefinition
{
    public const string Purpose = "function-gateway";

    public static ResourceIngressPolicyDefinition Gateway { get; } = new(
        FunctionResourceDeployment.ResourceType,
        Purpose,
        RequiresIngressPlacement: false,
        ports => ports.SequenceEqual([NetworkPort.Create(FunctionRevisionInKubernetes.AgentPort)]),
        request => ImmutableDictionary<string, string>.Empty
            .Add("pole.io/tenant-id", request.TenantId)
            .Add("pole.io/runtime-cell-id", request.RuntimeCellId)
            .Add("pole.io/resource-deployment-id", request.ResourceDeploymentId),
        (_, sources) => sources.Get(FunctionResourceDeployment.GatewaySourceName));
}
