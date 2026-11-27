// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Common;

internal static class ResourcePlacementExtensions
{
    public static ResourcePlacement ToPlacement(this RuntimeCell runtimeCell) => new(
        runtimeCell.ResourceGroupId,
        runtimeCell.IsolationScopeId,
        runtimeCell.Id,
        runtimeCell.DeploymentTargetId,
        runtimeCell.KubernetesNamespace,
        runtimeCell.DesiredGeneration,
        runtimeCell.State switch
        {
            ProvisioningRuntimeCellState => ResourcePlacementState.Provisioning,
            ReadyRuntimeCellState => ResourcePlacementState.Ready,
            FailedRuntimeCellState => ResourcePlacementState.Failed,
            DeletingRuntimeCellState => ResourcePlacementState.Deleting,
            DeletedRuntimeCellState => ResourcePlacementState.Deleted,
            _ => throw new InvalidOperationException(
                $"Unsupported runtime cell state '{runtimeCell.State.GetType().Name}'."),
        });
}
