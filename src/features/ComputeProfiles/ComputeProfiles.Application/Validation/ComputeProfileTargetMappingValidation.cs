using System.Globalization;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Validation;

public sealed record ComputeProfileTargetMappingsValid;

public union ValidateComputeProfileTargetMappingsOutcome(
    ComputeProfileTargetMappingsValid,
    ComputeProfileTargetMappingsInvalid);

public delegate Task<ValidateComputeProfileTargetMappingsOutcome>
    ValidateComputeProfileTargetMappings(
    ComputeProfile profile,
    CancellationToken cancellationToken = default);

public sealed class KubernetesComputeProfileTargetMappingValidator(
    IKubernetesClientProvider kubernetesClientProvider,
    AdmitDeploymentTargetForWorkload admitDeploymentTarget)
{
    private const string ArchitectureLabel = "kubernetes.io/arch";

    public async Task<ValidateComputeProfileTargetMappingsOutcome> InvokeAsync(
        ComputeProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        for (var index = 0; index < profile.TargetMappings.Count; index++)
        {
            var mapping = profile.TargetMappings[index];
            var validation = await ValidateMappingAsync(
                mapping,
                profile.Architecture,
                profile.ExtendedResources,
                index,
                cancellationToken);
            if (validation is ComputeProfileTargetMappingsInvalid error)
                return error;
        }

        return new ComputeProfileTargetMappingsValid();
    }

    private async Task<ValidateComputeProfileTargetMappingsOutcome> ValidateMappingAsync(
        ComputeProfileTargetMapping mapping,
        ComputeArchitecture architecture,
        IReadOnlyCollection<ComputeProfileExtendedResource> extendedResources,
        int mappingIndex,
        CancellationToken cancellationToken)
    {
        var targetResult = await admitDeploymentTarget(
            mapping.DeploymentTargetId,
            cancellationToken);
        if (targetResult is DeploymentTargetWorkloadAdmissionError)
        {
            return Invalid(
                mappingIndex,
                nameof(mapping.DeploymentTargetId),
                "Deployment target is unavailable for new workloads.");
        }

        IKubernetes kubernetes;
        try
        {
            kubernetes = await kubernetesClientProvider.GetAsync(
                mapping.DeploymentTargetId,
                cancellationToken);
        }
        catch (DeploymentTargetUnavailableException exception)
            when (exception.Reason is DeploymentTargetUnavailableReason.NotFound or
                DeploymentTargetUnavailableReason.Disabled)
        {
            return Invalid(
                mappingIndex,
                nameof(mapping.DeploymentTargetId),
                exception.Reason switch
                {
                    DeploymentTargetUnavailableReason.NotFound =>
                        "Deployment target does not exist.",
                    DeploymentTargetUnavailableReason.Disabled =>
                        "Deployment target is disabled.",
                    _ => "Deployment target is unavailable."
                });
        }

        var (runtimeClassFound, selector) = await LoadRuntimeClassSelectorAsync(
            kubernetes,
            mapping.RuntimeClassName,
            cancellationToken);
        if (!runtimeClassFound)
        {
            return Invalid(
                mappingIndex,
                nameof(mapping.RuntimeClassName),
                "Runtime class does not exist on the deployment target.");
        }

        var mergedSelector = MergeNodeSelectors(mapping, selector);
        if (mergedSelector is null)
        {
            return HardConstraintsFailure(
                mappingIndex,
                "Runtime class and compute profile contain conflicting node selectors.");
        }

        AddArchitectureSelector(mergedSelector, architecture);
        var nodes = await kubernetes.CoreV1.ListNodeAsync(
            cancellationToken: cancellationToken);
        var hasMatchingNode = nodes.Items.Any(node =>
            NodeMatches(
                node,
                mergedSelector,
                mapping.RequiredNodeAffinity,
                extendedResources));
        return hasMatchingNode
            ? new ComputeProfileTargetMappingsValid()
            : HardConstraintsFailure(
                mappingIndex,
                "No node satisfies the compute profile's hard constraints and extended resources.");
    }

    private static async Task<(bool Found, IReadOnlyDictionary<string, string> Selector)>
        LoadRuntimeClassSelectorAsync(
            IKubernetes kubernetes,
            string? runtimeClassName,
            CancellationToken cancellationToken)
    {
        if (runtimeClassName is null)
        {
            return (true, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        try
        {
            var runtimeClass = await kubernetes.NodeV1.ReadRuntimeClassAsync(
                runtimeClassName,
                cancellationToken: cancellationToken);
            return (
                true,
                new Dictionary<string, string>(
                    runtimeClass.Scheduling?.NodeSelector ??
                    new Dictionary<string, string>(),
                    StringComparer.Ordinal));
        }
        catch (HttpOperationException exception)
            when (exception.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return (false, new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    private static Dictionary<string, string>? MergeNodeSelectors(
        ComputeProfileTargetMapping mapping,
        IReadOnlyDictionary<string, string> runtimeClassSelector)
    {
        var result = new Dictionary<string, string>(runtimeClassSelector, StringComparer.Ordinal);
        foreach (var item in mapping.NodeSelector)
        {
            if (result.TryGetValue(item.Key, out var current) &&
                !string.Equals(current, item.Value, StringComparison.Ordinal))
            {
                return null;
            }

            result[item.Key] = item.Value;
        }

        return result;
    }

    private static void AddArchitectureSelector(
        IDictionary<string, string> selector,
        ComputeArchitecture architecture)
    {
        var expected = architecture switch
        {
            ComputeArchitecture.Any => null,
            ComputeArchitecture.Amd64 => "amd64",
            ComputeArchitecture.Arm64 => "arm64",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture))
        };
        if (expected is null)
            return;

        if (selector.TryGetValue(ArchitectureLabel, out var current) &&
            !string.Equals(current, expected, StringComparison.Ordinal))
        {
            selector[ArchitectureLabel] = "__pole-incompatible-architecture__";
            return;
        }

        selector[ArchitectureLabel] = expected;
    }

    private static bool NodeMatches(
        V1Node node,
        IReadOnlyDictionary<string, string> selector,
        IReadOnlyCollection<ComputeProfileNodeAffinityTerm> requiredAffinity,
        IReadOnlyCollection<ComputeProfileExtendedResource> extendedResources)
    {
        var labels = node.Metadata?.Labels ?? new Dictionary<string, string>();
        if (selector.Any(item =>
                !labels.TryGetValue(item.Key, out var value) ||
                !string.Equals(item.Value, value, StringComparison.Ordinal)))
        {
            return false;
        }

        if (requiredAffinity.Count > 0 &&
            !requiredAffinity.Any(term => TermMatches(term, labels)))
        {
            return false;
        }

        return extendedResources.All(resource =>
            node.Status?.Allocatable is not null &&
            node.Status.Allocatable.TryGetValue(resource.Name, out var quantity) &&
            KubernetesQuantityAtLeast(quantity.ToString(), resource.Quantity));
    }

    private static bool TermMatches(
        ComputeProfileNodeAffinityTerm term,
        IDictionary<string, string> labels) =>
        term.Expressions.All(expression => ExpressionMatches(expression, labels));

    private static bool ExpressionMatches(
        ComputeProfileNodeAffinityExpression expression,
        IDictionary<string, string> labels)
    {
        var hasValue = labels.TryGetValue(expression.Key, out var labelValue);
        return expression.Operator switch
        {
            ComputeNodeAffinityOperator.In =>
                hasValue && expression.Values.Contains(labelValue!, StringComparer.Ordinal),
            ComputeNodeAffinityOperator.NotIn =>
                !hasValue || !expression.Values.Contains(labelValue!, StringComparer.Ordinal),
            ComputeNodeAffinityOperator.Exists => hasValue,
            ComputeNodeAffinityOperator.DoesNotExist => !hasValue,
            ComputeNodeAffinityOperator.GreaterThan =>
                hasValue && CompareInteger(labelValue!, expression.Values[0], greaterThan: true),
            ComputeNodeAffinityOperator.LessThan =>
                hasValue && CompareInteger(labelValue!, expression.Values[0], greaterThan: false),
            _ => false
        };
    }

    private static bool CompareInteger(
        string labelValue,
        string expressionValue,
        bool greaterThan) =>
        long.TryParse(labelValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current) &&
        long.TryParse(expressionValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected) &&
        (greaterThan ? current > expected : current < expected);

    private static bool KubernetesQuantityAtLeast(
        string value,
        long required)
    {
        if (!TryParseKubernetesQuantity(value, out var actual))
            return false;
        return actual >= required;
    }

    private static bool TryParseKubernetesQuantity(
        string value,
        out decimal quantity)
    {
        quantity = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var suffixes = new (string Suffix, decimal Multiplier)[]
        {
            ("Ki", 1024m),
            ("Mi", 1024m * 1024m),
            ("Gi", 1024m * 1024m * 1024m),
            ("Ti", 1024m * 1024m * 1024m * 1024m),
            ("Pi", 1024m * 1024m * 1024m * 1024m * 1024m),
            ("k", 1_000m),
            ("M", 1_000_000m),
            ("G", 1_000_000_000m),
            ("T", 1_000_000_000_000m),
            ("P", 1_000_000_000_000_000m),
            ("m", 0.001m),
            ("u", 0.000001m),
            ("n", 0.000000001m)
        };
        foreach (var (suffix, multiplier) in suffixes)
        {
            if (!value.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            return decimal.TryParse(
                       value[..^suffix.Length],
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out var number) &&
                   TryMultiply(number, multiplier, out quantity);
        }

        return decimal.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out quantity);
    }

    private static bool TryMultiply(
        decimal value,
        decimal multiplier,
        out decimal result)
    {
        try
        {
            result = value * multiplier;
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static ValidateComputeProfileTargetMappingsOutcome HardConstraintsFailure(
        int mappingIndex,
        string message) =>
        Invalid(mappingIndex, "Scheduling", message);

    private static ValidateComputeProfileTargetMappingsOutcome Invalid(
        int mappingIndex,
        string field,
        string message) =>
        new ComputeProfileTargetMappingsInvalid(
            new Dictionary<string, string[]>
            {
                [Field(mappingIndex, field)] = [message]
            });

    private static string Field(int index, string name) =>
        $"TargetMappings[{index}].{name}";

}
