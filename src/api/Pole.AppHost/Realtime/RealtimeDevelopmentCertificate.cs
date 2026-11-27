// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pole.AppHost.Realtime;

internal static class RealtimeDevelopmentCertificate
{
    public static string GetOrCreate(string appHostDirectory)
    {
        var path = Path.GetFullPath(Path.Combine(appHostDirectory, "../../../.artifacts/realtime/key-protection.pfx"));
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=Pole local realtime key protection",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporaryPath, fileOptions))
            {
                file.Write(certificate.Export(X509ContentType.Pfx));
            }

            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Atomic rename ensures parallel AppHosts only see a complete certificate.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return path;
    }
}
