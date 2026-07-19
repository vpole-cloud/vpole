using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Pole.Encryption.Tests;

internal sealed class TemporaryKeyDirectory : IDisposable
{
    private readonly string _root;

    public TemporaryKeyDirectory()
    {
        _root = Path.Combine(Path.GetTempPath(), "pole-encryption-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string GetPath(string fileName) => Path.GetFullPath(Path.Combine(_root, fileName));

    public string WriteKey(string fileName, byte[]? key = null)
    {
        var path = GetPath(fileName);
        File.WriteAllText(path, Convert.ToBase64String(key ?? RandomNumberGenerator.GetBytes(32)));
        return path;
    }

    public string WriteText(string fileName, string contents)
    {
        var path = GetPath(fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        var fullRoot = Path.GetFullPath(_root);
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pole-encryption-tests"));
        if (!fullRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a test directory outside the expected temporary root.");

        if (Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }
}

internal sealed record TestKey(string Id, int Version, string Path);

internal sealed class TestEncryptionService : IDisposable
{
    private readonly LocalKeyFileKeyEncryptionProvider _provider;

    private TestEncryptionService(LocalKeyFileKeyEncryptionProvider provider)
    {
        _provider = provider;
        Service = new EnvelopeEncryptionService(
            [provider],
            Options.Create(new EnvelopeEncryptionOptions
            {
                ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId
            }));
    }

    public EnvelopeEncryptionService Service { get; }

    public static TestEncryptionService Create(string activeId, int activeVersion, params TestKey[] keys)
    {
        var providerOptions = new LocalKeyFileOptions
        {
            ActiveKeyId = activeId,
            ActiveKeyVersion = activeVersion,
            Keys = keys.Select(key => new LocalKeyFileKeyOptions
            {
                Id = key.Id,
                Version = key.Version,
                Path = key.Path
            }).ToList()
        };
        return new TestEncryptionService(
            new LocalKeyFileKeyEncryptionProvider(Options.Create(providerOptions)));
    }

    public void Dispose() => _provider.Dispose();
}

internal static class EncryptionTestData
{
    public static EnvelopeEncryptionContext TenantContext() =>
        EnvelopeEncryptionContext.ForTenant("tenant-a", "git-provider", "provider-42", "access-token", 7);

    public static EncryptedValue CloneEnvelope(EncryptedValue value) => value with
    {
        WrappedDataEncryptionKey = value.WrappedDataEncryptionKey.ToArray(),
        WrappedDataEncryptionKeyNonce = value.WrappedDataEncryptionKeyNonce.ToArray(),
        WrappedDataEncryptionKeyTag = value.WrappedDataEncryptionKeyTag.ToArray(),
        Ciphertext = value.Ciphertext.ToArray(),
        Nonce = value.Nonce.ToArray(),
        AuthenticationTag = value.AuthenticationTag.ToArray()
    };

    public static byte[] FlipFirstByte(byte[] value)
    {
        var clone = value.ToArray();
        clone[0] ^= 0x80;
        return clone;
    }
}
