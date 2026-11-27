// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pole.Cli.Authentication;

public static class CliAccountIdentity
{
    public static string GetAccountKey(
        string accessToken,
        Uri issuer,
        Uri server)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException(
                "The Pole access token does not contain an account identity. Run 'pole login' again.");

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty("sub", out var subject) ||
                subject.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(subject.GetString()))
                throw new InvalidOperationException(
                    "The Pole access token does not contain an account identity. Run 'pole login' again.");

            var identity = $"{server.GetLeftPart(UriPartial.Authority)}\n{issuer.AbsoluteUri}\n{subject.GetString()}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException(
                "The Pole access token has an invalid account identity. Run 'pole login' again.",
                error);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(
                "The Pole access token has an invalid account identity. Run 'pole login' again.",
                error);
        }
    }
}
