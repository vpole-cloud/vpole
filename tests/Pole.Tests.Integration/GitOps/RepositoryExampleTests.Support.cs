// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Compiler;
using Pole.Testing;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected CompiledSnapshot CompileExample(string example) =>
        Services.GetRequiredService<ManifestCompiler>().Compile(ReadExample(example));

    private protected static ManifestFile[] ReadExample(string example) => RepositoryExampleFiles
        .Read($"src/features/GitOps/examples/{example}/manifests")
        .Select(file => new ManifestFile(file.Path, file.Content))
        .ToArray();
}
