// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Pole.Encryption.Tests;

internal sealed class TemporaryKeyDirectory : IDisposable
{
    public TemporaryKeyDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "pole-encryption-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        var fullRoot = Path.GetFullPath(Root);
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pole-encryption-tests"));
        if (!fullRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Refusing to delete a test directory outside the expected temporary root.");

        if (Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }

    public string GetPath(string fileName) => Path.GetFullPath(Path.Combine(Root, fileName));

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
}

internal sealed record TestKey(
    string Id,
    int Version,
    string Path
);

internal sealed class TestEncryptionService : IDisposable
{
    private readonly LocalKeyFileKeyEncryptionProvider _provider;

    private TestEncryptionService(LocalKeyFileKeyEncryptionProvider provider)
    {
        _provider = provider;
        Service = new EnvelopeEncryptionService(
            [provider],
            Options.Create(
                new EnvelopeEncryptionOptions
                {
                    ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId,
                }));
    }

    public EnvelopeEncryptionService Service { get; }

    public void Dispose() => _provider.Dispose();

    public static TestEncryptionService Create(
        string activeId,
        int activeVersion,
        params TestKey[] keys)
    {
        var providerOptions = new LocalKeyFileOptions
        {
            ActiveKeyId = activeId,
            ActiveKeyVersion = activeVersion,
            Keys = keys.Select(key => new LocalKeyFileKeyOptions
                {
                    Id = key.Id,
                    Version = key.Version,
                    Path = key.Path,
                })
                .ToList(),
        };
        return new TestEncryptionService(new LocalKeyFileKeyEncryptionProvider(Options.Create(providerOptions)));
    }
}

internal static class EncryptionTestData
{
    public static EnvelopeEncryptionContext TenantContext() => EnvelopeEncryptionContext.ForTenant(
        "tenant-a",
        "git-provider",
        "provider-42",
        "access-token",
        7);

    public static EncryptedValue CloneEnvelope(EncryptedValue value) => value with
    {
        WrappedDataEncryptionKey = value.WrappedDataEncryptionKey.ToArray(),
        WrappedDataEncryptionKeyNonce = value.WrappedDataEncryptionKeyNonce.ToArray(),
        WrappedDataEncryptionKeyTag = value.WrappedDataEncryptionKeyTag.ToArray(),
        Ciphertext = value.Ciphertext.ToArray(),
        Nonce = value.Nonce.ToArray(),
        AuthenticationTag = value.AuthenticationTag.ToArray(),
    };

    public static byte[] FlipFirstByte(byte[] value)
    {
        var clone = value.ToArray();
        clone[0] ^= 0x80;
        return clone;
    }
}
