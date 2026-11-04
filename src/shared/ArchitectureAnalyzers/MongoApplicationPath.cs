namespace Pole.ArchitectureAnalyzers;

public enum MongoApplicationPath
{
    OutsideApplication,
    Application,
    Mongo
}

public static class MongoApplicationPaths
{
    public static MongoApplicationPath Classify(string path)
    {
        var segments = path.Replace('\\', '/').Split('/');
        var application = Array.FindIndex(segments, segment => segment.EndsWith(".Application", StringComparison.Ordinal));
        if (application < 0 || application == segments.Length - 1)
            return MongoApplicationPath.OutsideApplication;

        var firstFolder = application + 1;
        var file = segments.Length - 1;
        if (firstFolder == file || Array.IndexOf(segments, "Migrations", firstFolder, file - firstFolder) >= 0)
            return MongoApplicationPath.Application;

        // Persistence is accepted until all feature implementations have moved.
        if (segments[firstFolder] is "Mongo" or "Persistence")
            return MongoApplicationPath.Mongo;

        return segments[firstFolder] != "Endpoints" && firstFolder + 1 < file &&
            segments[firstFolder + 1] == "Mongo"
            ? MongoApplicationPath.Mongo
            : MongoApplicationPath.Application;
    }
}
