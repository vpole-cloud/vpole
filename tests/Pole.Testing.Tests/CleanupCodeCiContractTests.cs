// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Pole.Testing.Tests;

[Trait(TestTraits.Layer, TestTraits.Layers.Fast)]
[Trait(TestTraits.Concern, "CI")]
[Trait(TestTraits.Feature, "CleanupCode")]
public sealed class CleanupCodeCiContractTests
{
    [Fact]
    public void Shared_safe_profile_can_only_format_CSharp()
    {
        var root = RepositoryPaths.FindRoot();
        var settings = XDocument.Load(Path.Combine(root, "Pole.sln.DotSettings"));
        var entry = Assert.Single(
            settings.Root!.Elements(),
            element => element.Attributes()
                .Any(attribute => attribute.Name.LocalName == "Key" && attribute.Value.Contains(
                    "/=Pole_0020Safe_0020Cleanup/",
                    StringComparison.Ordinal)));
        var profile = XElement.Parse(entry.Value);
        var task = Assert.Single(profile.Elements());
        Assert.Equal("CSReformatCode", task.Name.LocalName);
        Assert.Equal("True", task.Value);
        Assert.Empty(task.Attributes());
        Assert.Empty(task.Elements());

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json")));
        var tool = manifest.RootElement.GetProperty("tools").GetProperty("jetbrains.resharper.globaltools");
        Assert.Equal("2026.3.0-eap08", tool.GetProperty("version").GetString());
        Assert.Contains(tool.GetProperty("commands").EnumerateArray(), command => command.GetString() == "jb");
    }

    [Fact]
    public void CI_restores_tools_before_the_non_mutating_format_check()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryPaths.FindRoot(), ".github", "workflows", "kubernetes-lab.yml"));
        var restore = workflow.IndexOf("run: dotnet tool restore", StringComparison.Ordinal);
        var check = workflow.IndexOf("scripts/quality_gate.py check-format", StringComparison.Ordinal);
        Assert.True(restore >= 0 && check > restore);
        Assert.Contains("10.0.x", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("scripts/quality_gate.py format --", workflow, StringComparison.Ordinal);
    }
}
