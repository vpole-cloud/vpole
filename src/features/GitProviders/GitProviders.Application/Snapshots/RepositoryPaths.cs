namespace Pole.Features.GitProviders.Application.Snapshots;

internal static class RepositoryPaths
{
    internal static bool Safe(string path) => path.Length > 0 && !path.Contains('\\') &&
        !path.Contains(':') && !path.Any(char.IsControl) &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));

    internal static bool Selected(string path, string directory) => directory.Length == 0 ||
        path.Equals(directory, StringComparison.Ordinal) || path.StartsWith(directory + "/", StringComparison.Ordinal);

    internal static bool Manifest(string path) => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
}
