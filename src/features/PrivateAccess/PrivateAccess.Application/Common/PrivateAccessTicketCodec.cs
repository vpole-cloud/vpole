// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Text;

namespace Pole.Features.PrivateAccess.Application.Common;

internal static class PrivateAccessTicketCodec
{
    private const string Prefix = "pole_pa_";
    private const int RandomByteCount = 32;

    public static (string Ticket, string Hash) Issue()
    {
        var ticket = Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(RandomByteCount));
        return (ticket, Hash(ticket));
    }

    public static bool TryHash(string? ticket, out string hash)
    {
        hash = "";
        if (ticket is null || ticket.Length != Prefix.Length + 2 * RandomByteCount ||
            !ticket.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        foreach (var character in ticket.AsSpan(Prefix.Length))
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;

        hash = Hash(ticket);
        return true;
    }

    private static string Hash(string ticket) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
