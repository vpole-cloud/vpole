using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Runtime.Gateway;

public static class FunctionHttpRouteMatcher
{
    public static FunctionHttpRouteBinding? Match(
        IEnumerable<FunctionHttpRouteBinding> routes,
        string handlerPath,
        string method,
        FunctionExposure exposure)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var requestSegments = TrySplitRequest(handlerPath);
        if (requestSegments is null)
            return null;
        return routes
            .Where(route => route.Exposure == exposure && route.Methods.Any(candidate =>
                string.Equals(candidate, method, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(route => Split(route.HandlerRoute).Count(IsParameter))
            .FirstOrDefault(route => Matches(Split(route.HandlerRoute), requestSegments));
    }

    private static bool Matches(string[] template, string[] request)
    {
        if (template.Length != request.Length)
            return false;

        for (var index = 0; index < template.Length; index++)
        {
            var segment = template[index];
            if (IsParameter(segment))
                continue;
            if (!string.Equals(segment, request[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string[] Split(string path) =>
        path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string[]? TrySplitRequest(string path)
    {
        try
        {
            var segments = Split(path)
                .Select(Uri.UnescapeDataString)
                .ToArray();
            return segments.Any(segment =>
                segment is "." or ".." ||
                segment.Contains('/') ||
                segment.Contains('\\') ||
                segment.Any(char.IsControl))
                ? null
                : segments;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static bool IsParameter(string segment) =>
        segment.Length >= 3 && segment[0] == '{' && segment[^1] == '}';
}
