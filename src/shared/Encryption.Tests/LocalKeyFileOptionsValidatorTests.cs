namespace Pole.Encryption.Tests;

public sealed class LocalKeyFileOptionsValidatorTests
{
    private readonly LocalKeyFileOptionsValidator _validator = new();

    [Fact]
    public void Valid_configuration_succeeds()
    {
        using var files = new TemporaryKeyDirectory();
        var options = Options("primary", 1, Key("primary", 1, files.WriteKey("primary.key")));

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Missing_configuration_fails()
    {
        var result = _validator.Validate(null, new LocalKeyFileOptions());

        Assert.True(result.Failed);
        Assert.Contains("ActiveKeyId", result.FailureMessage);
        Assert.Contains("ActiveKeyVersion", result.FailureMessage);
        Assert.Contains("Keys", result.FailureMessage);
    }

    [Fact]
    public void Missing_key_file_fails_without_generating_a_key()
    {
        using var files = new TemporaryKeyDirectory();
        var missingPath = files.GetPath("does-not-exist.key");
        var options = Options("primary", 1, Key("primary", 1, missingPath));

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.False(File.Exists(missingPath));
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new LocalKeyFileKeyEncryptionProvider(Microsoft.Extensions.Options.Options.Create(options)));
        Assert.DoesNotContain(missingPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(missingPath));
    }

    [Fact]
    public void Malformed_base64_key_file_fails()
    {
        using var files = new TemporaryKeyDirectory();
        var path = files.WriteText("malformed.key", "this is not base64 %%%");

        var result = _validator.Validate(null, Options("primary", 1, Key("primary", 1, path)));

        Assert.True(result.Failed);
        Assert.Contains("Base64-encoded 256-bit key file", result.FailureMessage);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Wrong_length_key_file_fails(int byteCount)
    {
        using var files = new TemporaryKeyDirectory();
        var path = files.WriteText("wrong-length.key", Convert.ToBase64String(new byte[byteCount]));

        var result = _validator.Validate(null, Options("primary", 1, Key("primary", 1, path)));

        Assert.True(result.Failed);
        Assert.Contains("256-bit", result.FailureMessage);
    }

    [Fact]
    public void Duplicate_key_identity_fails()
    {
        using var files = new TemporaryKeyDirectory();
        var options = Options(
            "primary",
            1,
            Key("primary", 1, files.WriteKey("one.key")),
            Key("primary", 1, files.WriteKey("two.key")));

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("IDs and versions must be unique", result.FailureMessage);
    }

    [Fact]
    public void Duplicate_key_path_fails()
    {
        using var files = new TemporaryKeyDirectory();
        var sharedPath = files.WriteKey("shared.key");
        var options = Options("one", 1, Key("one", 1, sharedPath), Key("two", 1, sharedPath));

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("paths must be unique", result.FailureMessage);
    }

    [Fact]
    public void Duplicate_key_material_fails()
    {
        using var files = new TemporaryKeyDirectory();
        var material = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        var options = Options(
            "one",
            1,
            Key("one", 1, files.WriteKey("one.key", material)),
            Key("two", 1, files.WriteKey("two.key", material)));

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("material must be unique", result.FailureMessage);
    }

    [Fact]
    public void Active_key_absent_from_configured_keys_fails()
    {
        using var files = new TemporaryKeyDirectory();
        var options = Options("primary", 2, Key("primary", 1, files.WriteKey("primary-v1.key")));

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("active key must identify exactly one", result.FailureMessage);
    }

    [Fact]
    public void Relative_key_path_fails()
    {
        var relativePath = Path.Combine("keys", "primary.key");
        var options = Options("primary", 1, Key("primary", 1, relativePath));

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Path must be an absolute path", result.FailureMessage);
    }

    [Fact]
    public void Validation_diagnostic_does_not_disclose_path_or_file_contents()
    {
        using var files = new TemporaryKeyDirectory();
        const string sensitiveContents = "malformed-private-key-material";
        var path = files.WriteText("customer-production-master-key.key", sensitiveContents);

        var result = _validator.Validate(null, Options("primary", 1, Key("primary", 1, path)));

        Assert.True(result.Failed);
        Assert.DoesNotContain(path, result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sensitiveContents, result.FailureMessage, StringComparison.Ordinal);
    }

    private static LocalKeyFileOptions Options(
        string activeId,
        int activeVersion,
        params LocalKeyFileKeyOptions[] keys) => new()
    {
        ActiveKeyId = activeId,
        ActiveKeyVersion = activeVersion,
        Keys = keys.ToList(),
    };

    private static LocalKeyFileKeyOptions Key(
        string id,
        int version,
        string path) => new()
    {
        Id = id,
        Version = version,
        Path = path,
    };
}
