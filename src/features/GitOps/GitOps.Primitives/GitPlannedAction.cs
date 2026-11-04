namespace Pole.Features.GitOps.Primitives;

public enum GitPlannedAction { Create, Update, NoOp, Blocked, Adopt, Replace, Remove, Detach }

public enum GitResourceMaterialization { Allocated, Created }
