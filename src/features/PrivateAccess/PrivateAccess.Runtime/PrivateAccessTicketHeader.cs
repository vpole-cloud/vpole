using Microsoft.AspNetCore.Http;

namespace Pole.Features.PrivateAccess.Runtime;

internal static class PrivateAccessTicketHeader
{
    public static string? Read(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } value ||
            !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = value[7..];
        return token.Length is > 0 and <= 256 ? token : null;
    }
}
