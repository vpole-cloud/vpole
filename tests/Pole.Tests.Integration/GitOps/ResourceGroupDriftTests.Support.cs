// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitOps.Compiler;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected static ManifestFile[] GroupSnapshot() =>
    [
        new(
            "group.yaml",
            """
            apiVersion: pole.io/v1alpha1
            kind: ResourceGroup
            metadata:
              name: drift-group
            spec:
              description: managed description
              tags: [production, api]
            """),
    ];

    private protected Task<long> BusinessEvents() => DbContext.OutboxMessages.CountDocumentsAsync(
        ScenarioFilter<OutboxMessage>(item => item.TenantId) & Builders<OutboxMessage>.Filter.Ne(
            item => item.EventType,
            "pole.gitops/v1/source-work-requested"));
}
