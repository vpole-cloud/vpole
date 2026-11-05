using Pole.Architecture.FSharp;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class RawBsonFSharpTests
{
    [Fact]
    public void SemanticCheck_DetectsAliasesAndInferredInvocationAndPropertyReturns()
    {
        const string source = """
            namespace Fixture
            open MongoDB.Bson
            module Cases =
                type Alias = BsonDocument
                let inferred () = Alias.Create()
                let property () = (inferred ()).Value
            """;

        var violations = Check(source);

        Assert.Contains(violations, item => item.Line == 4 && item.Message.Contains("MongoDB.Bson.BsonDocument"));
        Assert.Contains(violations, item => item.Line == 5 && item.Message.Contains("MongoDB.Bson.BsonDocument"));
        Assert.Contains(violations, item => item.Line == 6 && item.Message.Contains("MongoDB.Bson.BsonValue"));
    }

    [Fact]
    public void SemanticCheck_IgnoresUnrelatedNamesCommentsAndTripleQuotedStrings()
    {
        const string source = """"
            namespace Fixture
            module Cases =
                // MongoDB.Bson.BsonDocument()
                (* MongoDB.Bson.BsonArray() *)
                let text = """MongoDB.Bson.BsonValue
            let fake = MongoDB.Bson.BsonDocument()"""
                let unrelated () = Unrelated.BsonDocument()
            """";

        Assert.Empty(Check(source));
    }

    [Fact]
    public void MethodAllowance_CoversItsSignatureAndBodyWithoutCoveringCallersOrSiblings()
    {
        const string source = """
            namespace Fixture
            open MongoDB.Bson
            open Pole.Architecture
            module Cases =
                [<ViolateArchitecture("POLE014", "Server expression")>]
                let allowed () = BsonDocument.Create()
                let caller () = allowed ()
                let sibling () = BsonArray()
            """;

        var violations = Check(source);

        Assert.DoesNotContain(violations, item => item.Line <= 6);
        Assert.Contains(violations, item => item.Line == 7);
        Assert.Contains(violations, item => item.Line == 8);
    }

    [Fact]
    public void Task_member_exception_uses_the_source_binding_and_does_not_cover_siblings()
    {
        var violations = Check("""
            namespace Fixture
            open Pole.Architecture
            type Cases() =
                [<ViolateArchitecture("POLE014", "Aggregation within the task.")>]
                member _.Allowed() =
                    task {
                        let document = MongoDB.Bson.BsonDocument.Create()
                        return document
                    }
                member _.Sibling() = MongoDB.Bson.BsonDocument.Create()
            """);

        Assert.DoesNotContain(violations, item => item.Line <= 9);
        Assert.Contains(violations, item => item.Line == 10);
    }

    [Fact]
    public void TypeAllowance_CoversOwnedMembersWithoutCoveringAnotherType()
    {
        const string source = """
            namespace Fixture
            open MongoDB.Bson
            open Pole.Architecture
            [<ViolateArchitecture("POLE014", "Server expressions")>]
            type Allowed() =
                member _.Document = BsonDocument.Create()
                member _.Array() = BsonArray()
            type Sibling() =
                member _.Document = BsonDocument.Create()
            """;

        var violations = Check(source);

        Assert.DoesNotContain(violations, item => item.Line <= 7);
        Assert.Contains(violations, item => item.Line == 9);
    }

    [Fact]
    public void TypeAllowance_CoversRecordFieldSignatures()
    {
        const string source = """
            namespace Fixture
            open MongoDB.Bson
            open Pole.Architecture
            [<ViolateArchitecture("POLE014", "Driver data shape")>]
            type Allowed = { Value: BsonDocument; Values: BsonValue array }
            type Sibling = { Value: BsonDocument }
            """;

        var violations = Check(source);

        Assert.DoesNotContain(violations, item => item.Line <= 5);
        Assert.Contains(violations, item => item.Line == 6);
    }

    [Theory]
    [InlineData("POLE014", "")]
    [InlineData("POLE014", "   ")]
    [InlineData("*", "Blanket rule")]
    public void InvalidAllowance_IsReportedAndDoesNotSuppressRawTypes(string rule, string reason)
    {
        var source = $$"""
            namespace Fixture
            open MongoDB.Bson
            open Pole.Architecture
            module Cases =
                [<ViolateArchitecture("{{rule}}", "{{reason}}")>]
                let invalid () = BsonDocument.Create()
            """;

        var violations = Check(source);

        Assert.Contains(violations, item => item.Message.Contains("specific POLE rule ID and a nonblank reason"));
        Assert.Contains(violations, item => item.Line == 6 && item.Message.Contains("MongoDB.Bson.BsonDocument"));
    }

    [Fact]
    public void A_different_rule_does_not_suppress_BSON_and_is_not_invalid_metadata()
    {
        var violations = Check("""
            namespace Fixture
            open Pole.Architecture
            module Cases =
                [<ViolateArchitecture("POLE013", "A different rule.")>]
                let document () = MongoDB.Bson.BsonDocument.Create()
            """);

        Assert.Contains(violations, item => item.Line == 5);
        Assert.DoesNotContain(violations, item => item.Message.Contains("specific POLE rule ID"));
    }

    [Fact]
    public void AssemblyAllowance_DoesNotSuppressDeclarations()
    {
        const string source = """
            namespace Fixture
            open Pole.Architecture
            [<assembly: ViolateArchitecture("POLE014", "Too broad")>]
            do ()
            module Cases =
                let document () = MongoDB.Bson.BsonDocument.Create()
            """;

        Assert.Contains(Check(source), item => item.Line == 6);
    }

    [Fact]
    public void CompilerErrors_FailExplicitly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Check("namespace Fixture\nmodule Cases =\n    let missing = UnknownType()"));

        Assert.Contains("F# semantic checking failed", error.Message);
    }

    private static RawBsonViolation[] Check(string source)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pole-fsharp-architecture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var support = Path.Combine(directory, "Support.fs");
            var fixture = Path.Combine(directory, "Fixture.fs");
            File.WriteAllText(support, """
                namespace MongoDB.Bson
                type BsonValue() = class end
                type BsonDocument() =
                    inherit BsonValue()
                    member _.Value = BsonValue()
                    static member Create() = BsonDocument()
                type BsonArray() =
                    inherit BsonValue()
                namespace Pole.Architecture
                open System
                [<AttributeUsage(AttributeTargets.All)>]
                type ViolateArchitectureAttribute(rule: string, reason: string) =
                    inherit Attribute()
                namespace Unrelated
                type BsonDocument() = class end
                """);
            File.WriteAllText(fixture, source);

            var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                    ?? throw new InvalidOperationException("Runtime reference assemblies are unavailable."))
                .Split(Path.PathSeparator)
                .Append(System.Reflection.Assembly.Load("FSharp.Core").Location)
                .Distinct(StringComparer.Ordinal)
                .Select(path => "-r:" + path);
            var arguments = new[]
                { "--target:library", "--targetprofile:netcore", "--noframework", "--simpleresolution" }
                .Concat(references)
                .Concat([support, fixture])
                .ToArray();

            return RawBsonCheck.CheckProject(Path.Combine(directory, "Fixture.fsproj"), arguments)
                .Where(item => item.FilePath == fixture)
                .ToArray();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
