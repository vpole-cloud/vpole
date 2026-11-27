// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Pole.Encryption;

public sealed class LocalKeyFileOptionsValidator : IValidateOptions<LocalKeyFileOptions>
{
    private const int MaximumKeys = 64;

    public ValidateOptionsResult Validate(string? name, LocalKeyFileOptions options)
    {
        var failures = new List<string>();
        if (!IsValidKeyId(options.ActiveKeyId))
            failures.Add("EnvelopeEncryption:LocalKeyFile:ActiveKeyId is required and must not exceed 128 characters.");
        if (options.ActiveKeyVersion < 1)
            failures.Add("EnvelopeEncryption:LocalKeyFile:ActiveKeyVersion must be positive.");
        if (options.Keys is null || options.Keys.Count == 0)
            failures.Add("EnvelopeEncryption:LocalKeyFile:Keys must contain the active key.");
        else if (options.Keys.Count > MaximumKeys)
            failures.Add($"EnvelopeEncryption:LocalKeyFile:Keys cannot contain more than {MaximumKeys} keys.");

        var identities = new HashSet<KeyEncryptionKeyIdentity>();
        var paths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var loadedKeys = new List<byte[]>();
        try
        {
            foreach (var (key, index) in (options.Keys ?? []).Select((key, index) => (key, index)))
            {
                if (!IsValidKeyId(key.Id))
                    failures.Add(
                        $"EnvelopeEncryption:LocalKeyFile:Keys:{index}:Id is required and must not exceed 128 characters.");
                if (key.Version < 1)
                    failures.Add($"EnvelopeEncryption:LocalKeyFile:Keys:{index}:Version must be positive.");

                if (IsValidKeyId(key.Id) && key.Version > 0 &&
                    !identities.Add(new KeyEncryptionKeyIdentity(key.Id, key.Version)))
                    failures.Add("EnvelopeEncryption:LocalKeyFile key IDs and versions must be unique.");

                if (string.IsNullOrWhiteSpace(key.Path) || !Path.IsPathFullyQualified(key.Path))
                {
                    failures.Add($"EnvelopeEncryption:LocalKeyFile:Keys:{index}:Path must be an absolute path.");
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(key.Path);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                                      or PathTooLongException)
                {
                    failures.Add($"EnvelopeEncryption:LocalKeyFile:Keys:{index}:Path is invalid.");
                    continue;
                }

                if (!paths.Add(fullPath))
                    failures.Add("EnvelopeEncryption:LocalKeyFile key paths must be unique.");

                if (!LocalKeyFileReader.TryRead(fullPath, out var keyMaterial))
                {
                    failures.Add(
                        $"EnvelopeEncryption:LocalKeyFile:Keys:{index}:Path must identify a readable Base64-encoded 256-bit key file.");
                    continue;
                }

                if (loadedKeys.Any(existing => CryptographicOperations.FixedTimeEquals(existing, keyMaterial)))
                    failures.Add(
                        "EnvelopeEncryption:LocalKeyFile key material must be unique for every key ID and version.");
                loadedKeys.Add(keyMaterial);
            }

            var activeMatches = (options.Keys ?? []).Count(key =>
                string.Equals(key.Id, options.ActiveKeyId, StringComparison.Ordinal) &&
                key.Version == options.ActiveKeyVersion);
            if (activeMatches != 1)
                failures.Add("EnvelopeEncryption:LocalKeyFile active key must identify exactly one configured key.");

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
        finally
        {
            foreach (var key in loadedKeys)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool IsValidKeyId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

internal static class LocalKeyFileReader
{
    private const int KeySizeBytes = 32;
    private const long MaximumFileSizeBytes = 4096;

    public static bool TryRead(string path, out byte[] key)
    {
        key = [];
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > MaximumFileSizeBytes)
                return false;

            var encoded = File.ReadAllBytes(path);
            try
            {
                var encodedSpan = TrimAsciiWhitespace(encoded);
                var decoded = new byte[KeySizeBytes];
                var status = Base64.DecodeFromUtf8(
                    encodedSpan,
                    decoded,
                    out var consumed,
                    out var written,
                    isFinalBlock: true);
                if (status != OperationStatus.Done || consumed != encodedSpan.Length || written != KeySizeBytes)
                {
                    CryptographicOperations.ZeroMemory(decoded);
                    return false;
                }

                key = decoded;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException
                                              or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start]))
            start++;
        var end = value.Length;
        while (end > start && IsAsciiWhitespace(value[end - 1]))
            end--;
        return value[start..end];
    }

    private static bool IsAsciiWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
