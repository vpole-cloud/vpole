namespace Pole.Features.GitOps.Primitives;

public static class RepositoryManifestDirectory
{
    public static bool IsValid(string value) => value is not null &&
                                                (value.Length == 0 ||
                                                 (!value.Contains('\\') && !value.Contains(':') &&
                                                  !value.Any(char.IsControl) &&
                                                  value.Split('/').All(segment =>
                                                      segment.Length > 0 && segment is not ("." or ".."))));
}
