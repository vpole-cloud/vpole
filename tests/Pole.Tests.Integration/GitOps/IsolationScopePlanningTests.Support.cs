// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Compiler;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected static ManifestFile[] IsolationSnapshot(string group = "scope-group") =>
    [
        new(
            "scope.yaml",
            $$"""
              apiVersion: pole.io/v1alpha1
              kind: IsolationScope
              metadata:
                name: tenant-private
              spec:
                resourceGroup: {{group}}
              ---
              apiVersion: pole.io/v1alpha1
              kind: ResourceGroup
              metadata:
                name: {{group}}
              spec: {}
              """),
    ];
}
