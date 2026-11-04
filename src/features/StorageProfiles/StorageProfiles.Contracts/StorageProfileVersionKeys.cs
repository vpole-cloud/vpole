namespace Pole.Features.StorageProfiles.Contracts;

public static class StorageProfileVersionKeys
{
    public static string Policy(string profileId) => $"StorageProfile/{profileId}/Policy";
}
