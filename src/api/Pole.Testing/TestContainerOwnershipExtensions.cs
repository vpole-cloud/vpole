// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using DotNet.Testcontainers.Builders;

namespace Pole.Testing;

public static class TestContainerOwnershipExtensions
{
    public static ContainerBuilder WithTestOwnership(this ContainerBuilder builder, TestRunIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);

        return builder.WithLabel(TestRunIdentity.OwnerLabel, TestRunIdentity.Owner)
            .WithLabel(TestRunIdentity.WorktreeLabel, identity.WorktreeId)
            .WithLabel(TestRunIdentity.RunLabel, identity.RunId);
    }
}
