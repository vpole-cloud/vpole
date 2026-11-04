namespace Pole.Features.Functions.Application.Common;

internal static class FunctionTriggerRules
{
    public static readonly HashSet<string> AllowedHttpMethods =
        new(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], StringComparer.OrdinalIgnoreCase);

    public static bool IsValidHttpRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route) || route.Length > 200 || route.StartsWith('/') || route.EndsWith('/'))
            return false;

        var segments = route.Split('/');
        return segments.All(segment =>
            segment.Length > 0 &&
            segment is not "." and not ".." &&
            (IsLiteralSegment(segment) || IsParameterSegment(segment)));
    }

    public static string CanonicalizeRoute(string? route) =>
        string.Join('/', (route ?? string.Empty).Split('/').Select(segment =>
            IsParameterSegment(segment) ? "{}" : segment));

    private static bool IsLiteralSegment(string segment) =>
        char.IsLetterOrDigit(segment[0]) &&
        segment.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static bool IsParameterSegment(string segment) =>
        segment.Length >= 3 &&
        segment[0] == '{' &&
        segment[^1] == '}' &&
        char.IsLetter(segment[1]) &&
        segment[1..^1].All(character => char.IsLetterOrDigit(character) || character == '_');

}
