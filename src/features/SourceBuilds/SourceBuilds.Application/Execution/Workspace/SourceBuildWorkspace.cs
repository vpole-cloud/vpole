// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.SourceBuilds.Application.Execution.Workspace;

public sealed class SourceBuildWorkspace(
    string rootPath,
    string sourcePath
) : IAsyncDisposable
{
    public string RootPath { get; } = rootPath;
    public string SourcePath { get; } = sourcePath;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // A later workspace cleanup job can remove abandoned directories.
        }
        catch (UnauthorizedAccessException)
        {
            // A later workspace cleanup job can remove abandoned directories.
        }

        return ValueTask.CompletedTask;
    }
}
