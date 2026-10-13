namespace Pole.Features.StorageProfiles.Application.Observation;

public sealed class StorageObservationOptions
{
    public const string SectionName = "StorageProfiles:Observation";
    public bool Enabled { get; set; } = true;
    public int MaximumSources { get; init; } = 16;
    public int ScanBatchSize { get; init; } = 64;
    public int MaximumItemsPerKind { get; init; } = 2000;
    public int MaximumSnapshotBytes { get; init; } = 4 * 1024 * 1024;
    public bool Valid => MaximumSources is >= 1 and <= 128 && ScanBatchSize is >= 1 and <= 256 &&
        MaximumItemsPerKind is >= 1 and <= 5000 && MaximumSnapshotBytes is >= 1024 and <= 8 * 1024 * 1024;
}
