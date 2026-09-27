using System.Collections.Immutable;
using System.Globalization;
using Pole.Features.WebServices.Contracts;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

internal static class WebServiceConnectionEndpointCatalog
{
    public static ImmutableList<WebServiceConnectionEndpointDto> FromConfig(WebServiceConfig config) =>
        config.PortMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Name))
            .GroupBy(mapping => mapping.Name.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => ToEndpoint(group.Key, group.Single()))
            .OfType<WebServiceConnectionEndpointDto>()
            .OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal)
            .ToImmutableList();

    private static WebServiceConnectionEndpointDto? ToEndpoint(string id, PortMapping mapping)
    {
        if (!string.Equals(mapping.Protocol, "TCP", StringComparison.OrdinalIgnoreCase) ||
            mapping.ServicePort is < 1 or > 65535 ||
            !int.TryParse(mapping.TargetPort, NumberStyles.None, CultureInfo.InvariantCulture,
                out var targetPort) || targetPort is < 1 or > 65535)
        {
            return null;
        }
        return new WebServiceConnectionEndpointDto(id, mapping.ServicePort, targetPort);
    }
}
