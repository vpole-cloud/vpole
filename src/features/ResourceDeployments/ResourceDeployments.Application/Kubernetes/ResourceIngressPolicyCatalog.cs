using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Kubernetes;

internal sealed class ResourceIngressPolicyCatalog
{
    private readonly Dictionary<(ResourceTypeId ResourceType, string Purpose), ResourceIngressPolicyDefinition> definitions;

    public ResourceIngressPolicyCatalog(IEnumerable<ResourceIngressPolicyDefinition> contributions)
    {
        definitions = new Dictionary<(ResourceTypeId, string), ResourceIngressPolicyDefinition>();
        foreach (var definition in contributions)
        {
            if (definition.ResourceType == default ||
                string.IsNullOrWhiteSpace(definition.Purpose) ||
                definition.Purpose.Length > 63 ||
                definition.Purpose[0] is not (>= 'a' and <= 'z') ||
                definition.Purpose[^1] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
                definition.Purpose.Any(character => !char.IsAsciiLetterLower(character) &&
                                                    !char.IsAsciiDigit(character) && character != '-'))
            {
                throw new InvalidOperationException("Ingress policy contribution has an invalid identity.");
            }
            if (!definitions.TryAdd((definition.ResourceType, definition.Purpose), definition))
                throw new InvalidOperationException(
                    $"Duplicate ingress policy contribution: {definition.ResourceType}/{definition.Purpose}.");
        }
    }

    public ResourceIngressPolicyDefinition Resolve(ResourceIngressPolicyRequest request) =>
        definitions.TryGetValue((request.ResourceType, request.Purpose), out var definition)
            ? definition
            : throw new InvalidOperationException(
                $"No ingress policy contribution: {request.ResourceType}/{request.Purpose}.");
}
