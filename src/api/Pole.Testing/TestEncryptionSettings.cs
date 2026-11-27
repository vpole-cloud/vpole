// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Pole.Testing;

/// <summary>Owns a temporary encryption key for tests that compose platform services directly.</summary>
public sealed class TestEncryptionSettings : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "pole-test-encryption",
        Guid.NewGuid().ToString("N"));

    public TestEncryptionSettings()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "test.key");
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllText(path, Convert.ToBase64String(key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        Configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["EnvelopeEncryption:ActiveProviderId"] = "local-key-file",
                    ["EnvelopeEncryption:LocalKeyFile:ActiveKeyId"] = "test",
                    ["EnvelopeEncryption:LocalKeyFile:ActiveKeyVersion"] = "1",
                    ["EnvelopeEncryption:LocalKeyFile:Keys:0:Id"] = "test",
                    ["EnvelopeEncryption:LocalKeyFile:Keys:0:Version"] = "1",
                    ["EnvelopeEncryption:LocalKeyFile:Keys:0:Path"] = path,
                })
            .Build();
    }

    public IConfiguration Configuration { get; }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
