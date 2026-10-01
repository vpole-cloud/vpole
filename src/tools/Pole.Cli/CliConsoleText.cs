namespace Pole.Cli;

internal static class CliConsoleText
{
    public static string SafeValue(string? value, int maximumLength = 96)
    {
        if (value is null)
            return "(unknown)";
        var sanitized = new string(value.Take(maximumLength)
            .Select(character => char.IsControl(character) ? '?' : character)
            .ToArray());
        return value.Length > maximumLength ? sanitized + "…" : sanitized;
    }
}
