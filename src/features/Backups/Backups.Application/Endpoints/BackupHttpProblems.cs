using System.Globalization;
using System.Text.RegularExpressions;
using Pole.ASP.Helpers;

namespace Pole.Features.Backups.Application.Endpoints;

public sealed record BackupRequestRejected(
    string Code
);

public sealed record BackupDeletionAccepted;

public static partial class BackupHttpProblems
{
    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)$")]
    private static partial Regex ZonedTimestamp();

    public static bool TryTime(string text, out DateTimeOffset time)
    {
        time = default;
        return ZonedTimestamp().IsMatch(text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out time) &&
            time.UtcTicks % TimeSpan.TicksPerMicrosecond == 0;
    }

    internal static HttpProblem From(BackupRequestRejected error)
    {
        var status = error.Code switch
        {
            "not-found" => 404,
            "admin-required" => 403,
            "invalid-destination" or "invalid-policy" or "invalid-target-time" => 400,
            "idempotency-conflict" or "revision-conflict" or "identity-conflict" or "destination-in-use"
                or "archive-in-use" or "placement-conflict" or "destination-cleanup-pending" or "git-owned"
                or "destination-identity-immutable" or "policy-identity-immutable" => 409,
            "archive-observation-unavailable" => 503,
            _ when error.Code.Contains("catalog", StringComparison.OrdinalIgnoreCase) => 503,
            _ => 422,
        };
        return new(
            status,
            PoleProblemTypeId.FromCode($"backups.{error.Code}"),
            "Backup request could not be completed",
            $"backups.{error.Code}");
    }
}
