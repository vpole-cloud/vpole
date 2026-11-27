// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;

namespace Pole.Tests.E2E.Infrastructure;

internal static class ApiProblemCode
{
    internal static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int maximumBytes = 16_384;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[maximumBytes + 1];
            var count = await stream.ReadAtLeastAsync(
                buffer,
                buffer.Length,
                throwOnEndOfStream: false,
                cancellationToken);
            if (count > maximumBytes)
                return "unavailable";

            using var document = JsonDocument.Parse(
                buffer.AsMemory(0, count),
                new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 and <= 128 } code && code.Contains('.') && code.All(character =>
                    character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
                return code;
        }
        catch (Exception exception) when (exception is JsonException or IOException or HttpRequestException)
        {
            // Failure diagnostics must neither replace the HTTP outcome nor echo response contents.
        }

        return "unavailable";
    }
}
