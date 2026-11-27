// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Pole.ASP.Helpers;

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public class FromClaimAttribute(
    string claimType
) : Attribute, IBindingSourceMetadata
{
    public string ClaimType { get; } = claimType;

    public BindingSource BindingSource => BindingSource.Custom;
}
