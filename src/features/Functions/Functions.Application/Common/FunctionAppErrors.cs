namespace Pole.Features.Functions.Application.Common;

public sealed record FunctionAppNotFound(string FunctionAppId);

public sealed record FunctionAppDefaultDeploymentTargetUnavailable;

public sealed record FunctionAppHasPendingRevision(string FunctionAppId);

public sealed record FunctionAppIsNotEnabled(string FunctionAppId);
