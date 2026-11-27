// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.ArchitectureAnalyzers;

public enum MongoApplicationPath
{
    OutsideApplication,
    Application,
    EntryPoint,
}

public static class MongoApplicationPaths
{
    public static MongoApplicationPath Classify(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        var application = Array.FindIndex(
            segments,
            segment => segment.EndsWith(".Application", StringComparison.Ordinal));
        if (application < 0 || application == segments.Length - 1)
            return MongoApplicationPath.OutsideApplication;

        for (var index = application + 1; index < segments.Length - 1; index++)
            if (segments[index] is "Endpoints" or "Consumers" or "Jobs")
                return MongoApplicationPath.EntryPoint;

        return MongoApplicationPath.Application;
    }
}
