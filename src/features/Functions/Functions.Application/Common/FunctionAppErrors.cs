using Pole.Features.Functions.Application.Mongo;
namespace Pole.Features.Functions.Application.Common;

public sealed record FunctionAppNotFound(string FunctionAppId);

public sealed record FunctionAppDefaultDeploymentTargetUnavailable;

public sealed record FunctionAppHasPendingRevision(string FunctionAppId);

public sealed record FunctionAppIsNotEnabled(string FunctionAppId);

public sealed record FunctionAppResourceDeploymentMissing(string FunctionAppId);

public sealed record FunctionAppConfigurationUnavailable(string FunctionAppId);

public sealed record FunctionAppGitOwnedConfiguration(string FunctionAppId);
