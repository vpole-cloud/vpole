using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Pole.Encryption;

namespace Pole.Tests.Integration.Infrastructure;

internal static class TestEnvelopeEncryption
{
    public static IEnvelopeEncryptionService Create()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "pole-platform-encryption-integration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "platform-v1.key");
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllText(path, Convert.ToBase64String(key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var provider = new LocalKeyFileKeyEncryptionProvider(
            Options.Create(
                new LocalKeyFileOptions
                {
                    ActiveKeyId = "platform-integration",
                    ActiveKeyVersion = 1,
                    Keys =
                    [
                        new LocalKeyFileKeyOptions
                        {
                            Id = "platform-integration",
                            Version = 1,
                            Path = path,
                        },
                    ],
                }));
        return new EnvelopeEncryptionService(
            [provider],
            Options.Create(
                new EnvelopeEncryptionOptions
                {
                    ActiveProviderId = LocalKeyFileKeyEncryptionProvider.StableProviderId,
                }));
    }
}
