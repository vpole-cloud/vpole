namespace Pole.Features.Shared.Models;

public record ProbeSpec(
    ProbeConfig Startup,
    ProbeConfig Liveness,
    ProbeConfig Readiness
);

public record ProbeConfig(
    string Path,
    string Port,
    int InitialDelaySeconds = 10,
    int PeriodSeconds = 10,
    int TimeoutSeconds = 5,
    int FailureThreshold = 3,
    int SuccessThreshold = 1
);
