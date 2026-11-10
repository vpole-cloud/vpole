namespace Pole.Features.Shared.Models;

public record Resources(
    Resource Requests,
    Resource Limits
);

public record Resource(
    int CpuCores,
    int MemoryMi
);
