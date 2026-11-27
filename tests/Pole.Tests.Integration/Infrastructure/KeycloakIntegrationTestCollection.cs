// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[CollectionDefinition(Name)]
public sealed class KeycloakIntegrationTestCollection : ICollectionFixture<KeycloakContainerFixture>
{
    public const string Name = "keycloak-integration-tests";
}
