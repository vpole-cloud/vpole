using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.SourceBuilds.Application.Persistence.Configuration;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class DeclarativeBuildConfigurationTests
{
    private readonly DeclarativeSourceBuildConfigurationProvider provider =
        new(null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);

    [Fact]
    public void ExplicitRecipeNeedsNeitherAnalysisCandidateNorManifestPath()
    {
        var spec = JsonSerializer.SerializeToElement(new
        {
            repository = "application",
            webService = "api",
            recipe = new { dockerfile = "FROM scratch\n" }
        });
        var normalized = provider.NormalizeAndValidate(spec);
        Assert.Equal("FROM scratch\n", normalized.GetProperty("recipe").GetProperty("dockerfile").GetString());
        var facts = provider.Facts(normalized);
        Assert.False(facts.ReferencesShareScope);
        Assert.Contains(facts.References,
            item => item.Target == new ResourceAddress("SourceRepository", "application"));
        Assert.Contains(facts.References, item => item.Target == new ResourceAddress("WebService", "api"));
        var step = Assert.Single(provider.Steps(new("SourceBuildConfiguration", "api-build"), normalized));
        Assert.Equal(DeclarationPhase.Configure, step.Phase);
        Assert.All(step.Dependencies, item => Assert.Equal(DeclarationPhase.Prepare, item.Phase));
    }

    [Theory]
    [InlineData("{\"repository\":\"application\",\"webService\":\"api\",\"recipe\":{\"dockerfile\":\"\"}}")]
    [InlineData("{\"repository\":\"INVALID\",\"webService\":\"api\",\"recipe\":{\"dockerfile\":\"FROM scratch\"}}")]
    [InlineData("{\"repository\":\"application\",\"webService\":\"api\",\"recipe\":null}")]
    public void InvalidDeclarationIsRejectedBeforeMutation(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<DeclarationSchemaException>(() => provider.NormalizeAndValidate(document.RootElement));
    }

    [Fact]
    public void CandidateMetadataIsNotAnExecutableRecipeField()
    {
        using var document = JsonDocument.Parse("""
            {"repository":"application","webService":"api",
             "recipe":{"dockerfile":"FROM scratch","candidateKey":"candidate"}}
            """);
        Assert.Throws<JsonException>(() => provider.NormalizeAndValidate(document.RootElement));
    }
}
