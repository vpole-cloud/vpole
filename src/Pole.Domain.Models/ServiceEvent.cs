namespace Pole.Domain.Models;

public record ServiceEvent(
    DateTime OccurredOn,
    string Message
);