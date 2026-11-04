using System.Xml.Linq;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class FunctionSdkTestImageTests
{
    [Fact]
    public void FixtureSdkVersionMatchesTheControlPlaneProtocol()
    {
        var packages = XDocument.Load(Path.Combine(RepositoryPaths.FindRoot(), "Directory.Packages.props"));
        var protocol = packages.Descendants("PackageVersion")
            .Single(package => (string?)package.Attribute("Include") == "Pole.Functions.Protocol");
        Assert.Equal(FunctionSdkTestImage.SdkVersion, (string?)protocol.Attribute("Version"));
    }

    [Fact]
    public void BuildFailureDiagnosticsRemovePlaintextAndEncodedCredentials()
    {
        var sanitized = FunctionSdkTestImage.RedactBuildOutput(
            "compile error; password=fixture-password; auth=encoded-fixture-auth; fixture-password",
            ["fixture-password", "encoded-fixture-auth"]);
        Assert.Contains("compile error", sanitized);
        Assert.DoesNotContain("fixture-password", sanitized);
        Assert.DoesNotContain("encoded-fixture-auth", sanitized);
    }

    [Fact]
    public async Task TruncatedBuildOutputCannotExposeAPartialCredential()
    {
        var password = "private-fixture-password-" + new string('z', 1_000);
        using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            password + new string('x', 8_180) + "\ncompile error"));
        using var reader = new StreamReader(bytes);
        var output = await FunctionSdkTestImage.CaptureBuildOutputAsync(reader, [password]);
        Assert.True(output.Length <= 8_192);
        Assert.DoesNotContain("zz", output);
        Assert.Contains("compile error", output);
    }
}
