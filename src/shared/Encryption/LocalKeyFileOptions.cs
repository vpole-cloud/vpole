// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Encryption;

public sealed class LocalKeyFileOptions
{
    public const string SectionName = EnvelopeEncryptionOptions.SectionName + ":LocalKeyFile";

    public string ActiveKeyId { get; set; } = "";
    public int ActiveKeyVersion { get; set; }
    public List<LocalKeyFileKeyOptions> Keys { get; set; } = [];
}

public sealed class LocalKeyFileKeyOptions
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public string Path { get; set; } = "";
}
