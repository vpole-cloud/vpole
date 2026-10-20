using Pole.Testing;
using Xunit;

namespace Pole.Tests.Kubernetes;

[Trait(TestTraits.Layer, TestTraits.Layers.Integration)]
[Trait(TestTraits.Concern, "CI")]
[Trait(TestTraits.Feature, "KubernetesLab")]
public sealed class KubernetesLabCiContractTests
{
    [Fact]
    public void Github_workflow_protects_the_lab_from_runner_resource_pressure()
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "kubernetes-lab.yml");

        Assert.Contains("Reclaim runner disk space", workflow, StringComparison.Ordinal);
        Assert.Contains("/usr/local/lib/android", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "run: python3 scripts/quality_gate.py backend --results-directory .artifacts/coverage/backend/raw",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("run: dotnet test\n", workflow, StringComparison.Ordinal);
        Assert.Contains("set +e", workflow, StringComparison.Ordinal);
        Assert.Contains("if [[ ! -f \"${state}\" ]]", workflow, StringComparison.Ordinal);
        Assert.Contains("describe nodes", workflow, StringComparison.Ordinal);
        Assert.Contains("get pods --all-namespaces --output wide", workflow, StringComparison.Ordinal);
        Assert.Contains("get events --all-namespaces", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Lab_tool_version_checks_consume_complete_command_output()
    {
        var script = ReadRepositoryFile("infrastructure", "kubernetes-lab", "lab.sh");

        Assert.Contains(
            "awk '/k3d version/ { version = $3 } END { print version }'",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "awk '/k3d version/ { print $3; exit }'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "buildctl_version=\"$(\"${BUILDCTL_BIN}\" --version)\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "[[ \"${buildctl_version}\" == *\"${BUILDCTL_VERSION}\"* ]]",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "grep --fixed-strings --quiet \"${BUILDCTL_VERSION}\"",
            script,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.FindRoot(), .. segments]));
}
