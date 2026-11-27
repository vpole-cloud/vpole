// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Secrets.Application.Declarations;

internal static class SecretDeclarationRules
{
    public const int MaximumPurposeLength = 500;
    public const string ConsumerBindingRelation = "pole.secrets/consumer-binding";
}
