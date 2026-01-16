namespace Pole.Server.Features.Common;

public record BackgroundJob(
    string JobId,
    string? Description
)
{
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}