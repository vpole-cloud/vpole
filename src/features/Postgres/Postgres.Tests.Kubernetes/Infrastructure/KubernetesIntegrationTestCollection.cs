// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace Pole.Features.Postgres.Tests.Kubernetes.Infrastructure;

[CollectionDefinition(Name)]
public sealed class KubernetesIntegrationTestCollection : ICollectionFixture<KubernetesClusterFixture>
{
    public const string Name = KubernetesIntegrationTestBase.CollectionName;
}
