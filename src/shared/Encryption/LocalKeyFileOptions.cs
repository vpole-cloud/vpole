namespace Pole.Encryption;

public sealed class LocalKeyFileOptions
{
    public const string SectionName = EnvelopeEncryptionOptions.SectionName + ":LocalKeyFile";

    public string ActiveKeyId { get; set; } = string.Empty;
    public int ActiveKeyVersion { get; set; }
    public List<LocalKeyFileKeyOptions> Keys { get; set; } = [];
}

public sealed class LocalKeyFileKeyOptions
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Path { get; set; } = string.Empty;
}
