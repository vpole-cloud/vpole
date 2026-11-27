// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Compiler;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class RepositorySourceApiTestBase
{
    private protected static ManifestFile GroupManifest(string name) => new(
        $"{name}.yaml",
        $$"""
          apiVersion: pole.io/v1alpha1
          kind: ResourceGroup
          metadata: {name: {{name}}}
          spec: {}
          """);
}
