// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Users.Application.Identity;

public sealed class KeycloakOptions
{
    public const string SectionName = "KeycloakAdmin";

    public string BaseUrl { get; init; } = "";
    public string Realm { get; init; } = "";
    public string FrontendClientId { get; init; } = "pole.control-plane.ui";
    public string AdminClientId { get; init; } = "";
    public string AdminClientSecret { get; init; } = "";
}
