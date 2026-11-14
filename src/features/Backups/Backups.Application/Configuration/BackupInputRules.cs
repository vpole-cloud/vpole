using System.Text.RegularExpressions;

namespace Pole.Features.Backups.Application.Configuration;

public static partial class BackupInputRules
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,61}[a-z0-9]$|^[a-z]$")]
    private static partial Regex Name();

    public static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && Name().IsMatch(value);

    public static bool ValidEndpoint(string? endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 && uri.AbsolutePath == "/";

    public static bool ValidBucket(string? bucket) => bucket is { Length: >= 3 and <= 63 } &&
        Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9.-]*[a-z0-9]$");

    public static bool ValidPrefix(string? prefix) => prefix is { Length: <= 512 } && !prefix.Any(char.IsControl) &&
        prefix.Split('/').All(segment => segment != ".." && segment != ".");
}
