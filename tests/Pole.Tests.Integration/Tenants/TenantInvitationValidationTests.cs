// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.ASP.Helpers;
using Pole.Features.Tenants.Application.Invitations;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

public sealed class TenantInvitationValidationTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";

    private const string ValidInvitationId = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    public static TheoryData<string, bool> InvitationIds =>
        new()
        {
            { ValidInvitationId, true },
            { "", false },
            { ValidInvitationId.ToLowerInvariant(), false },
            { ValidInvitationId[..^1], false },
            { ValidInvitationId + "A", false },
            { ValidInvitationId + "\n", false },
        };

    [Theory]
    [MemberData(nameof(InvitationIds))]
    public void AllManagementValidatorsRequireTheSameCanonicalInvitationId(string id, bool valid)
    {
        Assert.Equal(64, ValidInvitationId.Length);
        var actor = new ClaimValue("actor");
        var body = new InvitationRevisionBody(1);
        Assert.Equal(
            valid,
            new RevokeTenantInvitationValidator().Validate(new RevokeTenantInvitationRequest(TenantId, id, body, actor))
                .IsValid);
        Assert.Equal(
            valid,
            new ResendTenantInvitationValidator().Validate(new ResendTenantInvitationRequest(TenantId, id, body, actor))
                .IsValid);
        Assert.Equal(
            valid,
            new UpdateTenantInvitationValidator().Validate(
                    new UpdateTenantInvitationRequest(
                        TenantId,
                        id,
                        new UpdateTenantInvitationBody(TenantRole.Member, 1),
                        actor))
                .IsValid);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789ABCDE_-", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789ABCDE_+", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789ABCDE_-\n", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789ABCDE_", false)]
    public void AllPublicValidatorsRequireTheSameBase64UrlToken(string token, bool valid)
    {
        var body = new InvitationTokenBody(token);
        Assert.Equal(
            valid,
            new PreviewTenantInvitationValidator().Validate(new PreviewTenantInvitationRequest(body)).IsValid);
        Assert.Equal(
            valid,
            new StartInvitationOnboardingValidator().Validate(new StartInvitationOnboardingRequest(body)).IsValid);
        var accept = new AcceptTenantInvitationRequest(
            body,
            new("subject"),
            new("invited@example.test"),
            new("true"),
            default,
            default);
        Assert.Equal(valid, new AcceptTenantInvitationValidator().Validate(accept).IsValid);
    }
}
