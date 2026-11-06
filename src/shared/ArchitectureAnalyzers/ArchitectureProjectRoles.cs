namespace Pole.ArchitectureAnalyzers;

public static class ArchitectureProjectRoles
{
    public const string Production = "Production";
    public const string Migration = "Migration";
    public const string TestSupport = "TestSupport";

    public static bool ExemptsMongoRules(string? role, bool isTestProject) =>
        isTestProject || role is Migration or TestSupport;
}
