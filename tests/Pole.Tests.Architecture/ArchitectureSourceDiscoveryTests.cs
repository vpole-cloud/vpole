// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace Pole.Tests.Architecture;

public sealed class ArchitectureSourceDiscoveryTests
{
    [Theory]
    [InlineData("*.cs", ".cs")]
    [InlineData("*.fs", ".fs")]
    public void Source_discovery_prunes_generated_trees_and_keeps_similarly_named_source_directories(
        string pattern,
        string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pole-architecture-sources-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] included =
            [
                "src/Domain", "src/.artifacts-example/Sample", "src/artifacts/Sample",
                "src/.agents-example/Sample", "src/agents/Sample",
            ];
            string[] excluded =
            [
                ".artifacts/runtime/context/Program", ".agents/local/Program",
                "src/bin/Debug/Generated", "src/obj/Generated",
            ];
            foreach (var relative in included.Concat(excluded))
            {
                var path = Path.Combine(directory, relative + extension);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "// source discovery fixture");
            }

            var discovered = ArchitectureSources.SourceFiles(directory, pattern)
                .Select(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(included.Select(path => path + extension).Order(StringComparer.Ordinal), discovered);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
