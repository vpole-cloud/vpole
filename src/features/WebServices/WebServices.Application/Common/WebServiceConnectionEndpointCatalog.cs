// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Common;

internal static class WebServiceConnectionEndpointCatalog
{
    public static ImmutableList<ResourceConnectionEndpoint> FromConfig(WebServiceConfig config, string serviceName) =>
        config.PortMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Name))
            .GroupBy(mapping => mapping.Name.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => ToEndpoint(group.Key, serviceName, group.Single()))
            .OfType<ResourceConnectionEndpoint>()
            .OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal)
            .ToImmutableList();

    private static ResourceConnectionEndpoint? ToEndpoint(
        string id,
        string serviceName,
        PortMapping mapping)
    {
        if (!NetworkPort.TryCreate(mapping.ServicePort, out var servicePort) || !WebServicePublicIngressPort.TryResolve(
                mapping.Protocol,
                mapping.TargetPort,
                out var targetPort))
            return null;
        return new ResourceConnectionEndpoint(id, serviceName, servicePort, targetPort)
        {
            DisplayName = mapping.Name.Trim(),
            Protocol = "tcp",
        };
    }
}
