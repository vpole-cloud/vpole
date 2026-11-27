// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Core.Kubernetes;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public static class RedisInKubernetesExtensions
{
    extension(Redis redis)
    {
        public RedisInKubernetes ToKubernetes(
            string kubernetesNamespace,
            ResourceDeploymentStorageConfiguration storageConfiguration,
            ResourceDeploymentComputeConfiguration computeConfiguration,
            WorkloadIdentityLabels placementIdentity) => RedisInKubernetes.Create(
            redis,
            kubernetesNamespace,
            storageConfiguration,
            computeConfiguration,
            placementIdentity);
    }
}
