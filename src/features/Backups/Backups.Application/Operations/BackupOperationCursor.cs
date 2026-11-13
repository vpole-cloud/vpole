using System.Globalization;
using System.Text;
using MongoDB.Bson;

namespace Pole.Features.Backups.Application.Operations;

public readonly record struct BackupOperationCursor(
    DateTime CreatedAtUtc,
    string Id
)
{
    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CreatedAtUtc.Ticks:x}:{Id}"));

    public static bool TryParse(string? value, out BackupOperationCursor cursor)
    {
        cursor = default;
        if (value is null || value.Length > 128)
            return false;
        Span<byte> bytes = stackalloc byte[96];
        if (!Convert.TryFromBase64String(value, bytes, out var length))
            return false;
        var parts = Encoding.UTF8.GetString(bytes[..length]).Split(':');
        if (parts.Length != 2 || !ObjectId.TryParse(parts[1], out _) ||
            !long.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ||
            ticks % TimeSpan.TicksPerMillisecond != 0)
            return false;
        cursor = new BackupOperationCursor(new DateTime(ticks, DateTimeKind.Utc), parts[1]);
        return true;
    }
}
