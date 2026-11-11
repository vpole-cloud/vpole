using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var input = JsonSerializer.Deserialize<SourcePair[]>(File.ReadAllText(args.Single()))!;
var result = input.Select(pair => Equivalent(pair.Before, pair.After)).ToArray();
Console.WriteLine(JsonSerializer.Serialize(result));

static bool Equivalent(string before, string after)
{
    var options = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse);
    var left = CSharpSyntaxTree.ParseText(before, options);
    var right = CSharpSyntaxTree.ParseText(after, options);
    if (left.GetDiagnostics().Concat(right.GetDiagnostics()).Any(item => item.Severity == DiagnosticSeverity.Error))
        return false;

    return Parts(left).SequenceEqual(Parts(right));
}

static IEnumerable<(int Kind, string Text)> Parts(SyntaxTree tree)
{
    foreach (var token in tree.GetRoot().DescendantTokens())
    {
        foreach (var trivia in token.LeadingTrivia)
            if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                yield return (trivia.RawKind, trivia.ToFullString());

        yield return (token.RawKind, token.Text);

        foreach (var trivia in token.TrailingTrivia)
            if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                yield return (trivia.RawKind, trivia.ToFullString());
    }
}

internal sealed record SourcePair(
    string Before,
    string After
);
