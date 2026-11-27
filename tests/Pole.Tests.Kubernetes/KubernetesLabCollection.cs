// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Testing;
using Xunit;

namespace Pole.Tests.Kubernetes;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KubernetesLabCollection : ICollectionFixture<KubernetesClusterFixture>
{
    public const string Name = KubernetesIntegrationTestBase.CollectionName;
}
