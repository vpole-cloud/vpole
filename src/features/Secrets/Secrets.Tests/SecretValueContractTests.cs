using Pole.Features.Secrets.Application.Endpoints;
using Pole.Features.Secrets.Contracts;
using Xunit;

namespace Pole.Features.Secrets.Tests;

public sealed class SecretValueContractTests
{
    private static bool Valid(Dictionary<string, string> values) => new RotateSecretValidator().Validate(
        new RotateSecretRequest("507f1f77bcf86cd799439011", new(0, values), "507f1f77bcf86cd799439012")).IsValid;

    [Theory]
    [InlineData("API_TOKEN", true)]
    [InlineData("api.token-1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("ключ", false)]
    public void Secret_key_references_and_value_submission_share_the_same_syntax(string key, bool expected)
    {
        Assert.Equal(expected, SecretKeyReferenceRules.ValidKey(key));
        Assert.Equal(expected, Valid(new() { [key] = "" }));
    }

    [Fact]
    public void Metadata_only_first_value_accepts_empty_string_and_exact_key_bounds()
    {
        Assert.True(Valid(new() { [new string('A', 128)] = "" }));
        Assert.False(Valid(new() { [new string('A', 129)] = "" }));
        var values = Enumerable.Range(0, 64).ToDictionary(index => "KEY_" + index, _ => "");
        Assert.True(Valid(values));
        values.Add("ONE_TOO_MANY", "");
        Assert.False(Valid(values));
    }

    [Fact]
    public void Preparation_request_diagnostics_redact_values_even_when_the_dictionary_prints_them()
    {
        var request = new PrepareManagedSecretRequest(
            "tenant", "secret", new("pole/postgresql", "cluster"), "credentials", 1,
            new DiagnosticValues { ["password"] = "synthetic-sensitive-value" }, ManagedSecretKubernetesType.BasicAuthentication);
        Assert.DoesNotContain("synthetic-sensitive-value", request.ToString());
        Assert.Contains("[REDACTED]", request.ToString());
    }

    private sealed class DiagnosticValues : Dictionary<string, string>
    {
        public override string ToString() => this["password"];
    }

    [Fact]
    public void Value_budget_counts_UTF8_bytes_instead_of_characters()
    {
        var atLimit = new string('é', 32768);
        Assert.True(Valid(new() { ["TOKEN"] = atLimit }));
        Assert.False(Valid(new() { ["TOKEN"] = atLimit + 'é' }));
    }
}
