using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Application.Persistence.Repositories;
using Xunit;

namespace Pole.Features.GitProviders.Tests;

public sealed class SourceRepositoryDeclarationTests
{
    private static DeclarativeSourceRepositoryProvider Provider() =>
        new(null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);

    [Fact]
    public void NormalizesRepositoryIdentityAndOnlyContributesPrepare()
    {
        var provider = Provider();
        var spec = provider.NormalizeAndValidate(DeclarationJson.Write(
            new SourceRepositoryDeclaration("production", "0042", "feature/application")));
        var declaration = DeclarationJson.Read<SourceRepositoryDeclaration>(spec);
        Assert.Equal("42", declaration.RepositoryId);
        Assert.Equal("feature/application", declaration.Branch);
        var step = Assert.Single(provider.Steps(new("SourceRepository", "application"), spec));
        Assert.Equal(DeclarationPhase.Prepare, step.Phase);
        Assert.Empty(step.Dependencies);
    }

    [Theory]
    [InlineData("BadName", "42", "main", "spec.connection")]
    [InlineData("production", "0", "main", "spec.repositoryId")]
    [InlineData("production", "../repo", "main", "spec.repositoryId")]
    [InlineData("production", "42", " ", "spec.branch")]
    public void RejectsInvalidIdentityFields(string connection, string repository, string branch, string field)
    {
        var error = Assert.Throws<DeclarationSchemaException>(() => Provider().NormalizeAndValidate(
            DeclarationJson.Write(new SourceRepositoryDeclaration(connection, repository, branch))));
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void RejectsCandidateAndCredentialFields()
    {
        Assert.Throws<JsonException>(() => Provider().NormalizeAndValidate(JsonSerializer.SerializeToElement(new
        {
            connection = "production",
            repositoryId = "42",
            branch = "main",
            candidateKey = "candidate"
        })));
        Assert.Throws<JsonException>(() => Provider().NormalizeAndValidate(JsonSerializer.SerializeToElement(new
        {
            connection = "production",
            repositoryId = "42",
            branch = "main",
            token = "forbidden"
        })));
    }
}
