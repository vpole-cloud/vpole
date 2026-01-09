namespace Pole.Server.Models;

public record BackgroundJob(
    string JobId,
    string? Description
)
{
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}