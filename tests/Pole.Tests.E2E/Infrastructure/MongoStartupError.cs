using System.Text.Json;

namespace Pole.Tests.E2E.Infrastructure;

internal sealed record MongoStartupError(
    long EventId,
    int? Code,
    string Cause
)
{
    // Resource logs can contain credentials. Only numeric IDs and fixed categories leave this parser.
    internal static MongoStartupError? Parse(string line)
    {
        if (line.Length > 16_384)
            return null;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("s", out var severity) ||
                severity.ValueKind != JsonValueKind.String || severity.GetString() is not ("E" or "F") ||
                !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var eventId))
                return null;

            int? code = null;
            var cause = "unclassified";
            if (root.TryGetProperty("attr", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
                attributes.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var errorCode) &&
                        errorCode.ValueKind == JsonValueKind.Number && errorCode.TryGetInt32(out var value))
                        code = value;
                    if (error.TryGetProperty("errmsg", out var message) && message.ValueKind == JsonValueKind.String)
                        cause = Classify(message.GetString()!);
                }
                else if (error.ValueKind == JsonValueKind.String)
                {
                    cause = Classify(error.GetString()!);
                }
            }

            return new MongoStartupError(eventId, code, cause);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Classify(string error) => error switch
    {
        _ when error.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) => "address-in-use",
        _ when error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) => "permission-denied",
        _ when error.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) => "disk-full",
        _ when error.Contains("Cannot assign requested address", StringComparison.OrdinalIgnoreCase) =>
            "address-unavailable",
        _ => "unclassified",
    };

    public override string ToString() =>
        $"MongoDB error: event={EventId}, code={Code?.ToString() ?? "none"}, cause={Cause}";
}
