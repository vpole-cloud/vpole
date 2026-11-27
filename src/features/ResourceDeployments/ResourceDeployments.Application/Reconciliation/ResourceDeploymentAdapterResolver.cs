// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

public delegate (IResourceDeploymentAdapter Adapter, ResourcePlacementCapability PlacementCapability)
    ResolveResourceDeploymentAdapter(string resourceType);

internal sealed class ResourceDeploymentAdapterResolver(
    ResourceDeploymentAdapterCatalog catalog,
    IServiceProvider serviceProvider
)
{
    public (IResourceDeploymentAdapter Adapter, ResourcePlacementCapability PlacementCapability) Resolve(
        string resourceType)
    {
        var descriptor = catalog.GetRequired(resourceType);
        var adapter = serviceProvider.GetRequiredService(descriptor.AdapterType);
        if (adapter is not IResourceDeploymentAdapter typedAdapter)
            throw new InvalidOperationException(
                $"Registered resource deployment adapter '{descriptor.AdapterType.FullName}' does not implement {nameof(IResourceDeploymentAdapter)}.");

        return (typedAdapter, descriptor.PlacementCapability);
    }
}
