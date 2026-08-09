namespace Pole.Features.ComputeProfiles.Primitives;

public enum ComputeArchitecture
{
    Any = 0,
    Amd64 = 1,
    Arm64 = 2
}

public enum ComputeNodeAffinityOperator
{
    In = 0,
    NotIn = 1,
    Exists = 2,
    DoesNotExist = 3,
    GreaterThan = 4,
    LessThan = 5
}

public enum ComputeTolerationOperator
{
    Equal = 0,
    Exists = 1
}

public enum ComputeTaintEffect
{
    Any = 0,
    NoSchedule = 1,
    PreferNoSchedule = 2,
    NoExecute = 3
}
