namespace Pole.Features.ComputeProfiles.Contracts;

public static class ComputeProfileVersionKeys
{
    public static string Policy(string profileId) => $"ComputeProfile/{profileId}/Policy";

    public static string Revision(string profileId, long revision) => $"ComputeProfile/{profileId}/Revision/{revision}";
}
