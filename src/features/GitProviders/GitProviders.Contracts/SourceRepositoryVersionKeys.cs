namespace Pole.Features.GitProviders.Contracts;

public static class SourceRepositoryVersionKeys
{
    public static string Revision(string id) => $"SourceRepository/{id}";
}
