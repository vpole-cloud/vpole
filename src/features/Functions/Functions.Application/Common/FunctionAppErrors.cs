namespace Pole.Features.Functions.Application.Common;

public sealed record FunctionAppNotFound(string FunctionAppId);

public sealed record FunctionAppResourceGroupNotFound(string ResourceGroupId);

public sealed record FunctionAppResourceGroupDeleting(string ResourceGroupId);

public sealed record FunctionAppDefaultDeploymentTargetUnavailable;

public sealed record FunctionAppHasPendingRevision(string FunctionAppId);

public sealed record FunctionAppIsNotEnabled(string FunctionAppId);
