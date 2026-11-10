using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Pole.Testing;

namespace Pole.Tests.E2E.Infrastructure;

internal static class PhaseRequestEvidence
{
    internal static void Record(
        TestPhaseJournal? journal,
        TestPhase phase,
        Uri? uri,
        long request,
        int? status = null)
    {
        if (journal is null || uri is null)
            return;
        // Relative API paths use a fixed synthetic origin only for parsing; origins are never recorded.
        if (!uri.IsAbsoluteUri)
            uri = new Uri(new Uri("https://evidence.invalid"), uri);
        if (!uri.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal))
            return;
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = QueryHelpers.ParseQuery(uri.Query);
        journal.Record(
            phase,
            resourceId: path.Length > 3 ? path[3] : null,
            instanceUid: query.TryGetValue("instanceUid", out var uid) ? uid.ToString() : null,
            container: query.TryGetValue("container", out var container) ? container.ToString() : null,
            request: request,
            status: status,
            revision: query.TryGetValue("expectedRevision", out var revision) && long.TryParse(
                revision.ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number) && number > 0
                ? number
                : null);
    }
}
