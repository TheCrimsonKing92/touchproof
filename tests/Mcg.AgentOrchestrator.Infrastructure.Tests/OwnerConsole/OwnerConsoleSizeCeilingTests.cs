using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: read-only verified repository inspection; all controls are in-memory sources.
public sealed class OwnerConsoleSizeCeilingTests
{
    [Fact]
    public void ConsoleFiles_StayWithinTheirCeilings()
    {
        Assert.Empty(FileViolations(Sources(), OwnerConsoleSizeBaseline.Files));
    }

    [Fact]
    public void ConsoleMethods_StayWithinTheirCeilings()
    {
        Assert.Empty(MethodViolations(Sources(), OwnerConsoleSizeBaseline.Methods));
    }

    [Fact]
    public void NewFileWith251Lines_IsRejected()
    {
        var sources = new Dictionary<string, string> { ["View.cs"] = new('\n', 251) };
        Assert.Equal(["View.cs: 251 lines exceeds ceiling 250"], FileViolations(sources, new Dictionary<string, int>()));
    }

    [Fact]
    public void NewFileWith250Lines_Passes()
    {
        var sources = new Dictionary<string, string> { ["View.cs"] = new('\n', 250) };
        Assert.Empty(FileViolations(sources, new Dictionary<string, int>()));
    }

    [Fact]
    public void BaselineFileOneLineOverCeiling_NamesTheFile()
    {
        var sources = new Dictionary<string, string> { ["Large.cs"] = new('\n', 301) };
        Assert.Equal(["Large.cs: 301 lines exceeds ceiling 300"],
            FileViolations(sources, new Dictionary<string, int> { ["Large.cs"] = 300 }));
    }

    [Fact]
    public void MissingBaselineFile_IsRejected()
    {
        Assert.Equal(["Gone.cs: stale file baseline"],
            FileViolations(new Dictionary<string, string>(), new Dictionary<string, int> { ["Gone.cs"] = 300 }));
    }

    [Fact]
    public void ShrunkenBaselineFile_ReportsLowerableRow()
    {
        Assert.Equal(["Large.cs: lower baseline ceiling from 300 to 299"],
            FileViolations(new Dictionary<string, string> { ["Large.cs"] = new('\n', 299) },
                new Dictionary<string, int> { ["Large.cs"] = 300 }));
    }

    [Fact]
    public void MethodWith61Lines_IsRejected()
    {
        Assert.Equal(["View.cs::View.Draw: 61 lines exceeds ceiling 60"],
            MethodViolations(new Dictionary<string, string> { ["View.cs"] = MethodSource(61) }, []));
    }

    [Fact]
    public void MethodWith60Lines_Passes()
    {
        Assert.Empty(MethodViolations(new Dictionary<string, string> { ["View.cs"] = MethodSource(60) }, []));
    }

    [Fact]
    public void ShortenedBaselineMethod_ReportsRemovableRow()
    {
        Assert.Equal(["View.cs::View.Draw: removable method baseline (at most 60 lines)"],
            MethodViolations(new Dictionary<string, string> { ["View.cs"] = MethodSource(60) }, ["View.cs::View.Draw"]));
    }

    [Fact]
    public void MissingBaselineMethod_IsRejected()
    {
        Assert.Equal(["View.cs::View.Draw: stale method baseline"],
            MethodViolations(new Dictionary<string, string> { ["View.cs"] = "class View {}" }, ["View.cs::View.Draw"]));
    }

    private static Dictionary<string, string> Sources()
    {
        var folder = Path.Combine(VerifiedRepositoryRoot.Find(), "src/Mcg.AgentOrchestrator.App/Console");
        var sources = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(folder, path).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
        Assert.NotEmpty(sources);
        return sources;
    }

    private static IReadOnlyList<string> FileViolations(IReadOnlyDictionary<string, string> sources,
        IReadOnlyDictionary<string, int> baseline)
    {
        var findings = baseline.Keys.Where(path => !sources.ContainsKey(path)).Order(StringComparer.Ordinal)
            .Select(path => $"{path}: stale file baseline").ToList();
        foreach (var (path, source) in sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var ceiling = baseline.TryGetValue(path, out var value) ? value : 250;
            var lines = LineCount(source);
            if (lines > ceiling) findings.Add($"{path}: {lines} lines exceeds ceiling {ceiling}");
            else if (baseline.ContainsKey(path) && lines < ceiling)
                findings.Add($"{path}: lower baseline ceiling from {ceiling} to {lines}");
        }
        return findings;
    }

    private static int LineCount(string source)
    {
        using var reader = new StringReader(source);
        var lines = 0;
        while (reader.ReadLine() is not null) lines++;
        return lines;
    }

    private static IReadOnlyList<string> MethodViolations(IReadOnlyDictionary<string, string> sources, IEnumerable<string> baseline)
    {
        var exemptions = baseline.ToHashSet(StringComparer.Ordinal);
        var measured = new Dictionary<string, int>(StringComparer.Ordinal);
        var findings = new List<string>();
        foreach (var (path, source) in sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        foreach (var method in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
                     .Where(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax))
        {
            var key = path + "::" + string.Join(".", method.Ancestors().Reverse()
                .Where(node => node is BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax)
                .Select(DeclaredName).Append(DeclaredName(method)));
            var span = method.GetLocation().GetLineSpan();
            var lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
            measured[key] = Math.Max(measured.GetValueOrDefault(key), lines);
            if (lines > 60 && !exemptions.Contains(key)) findings.Add($"{key}: {lines} lines exceeds ceiling 60");
        }
        foreach (var key in exemptions.Order(StringComparer.Ordinal))
        {
            if (!measured.TryGetValue(key, out var lines)) findings.Add($"{key}: stale method baseline");
            else if (lines <= 60) findings.Add($"{key}: removable method baseline (at most 60 lines)");
        }
        return findings;
    }

    private static string DeclaredName(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.ValueText,
        OperatorDeclarationSyntax @operator => "operator " + @operator.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax conversion => conversion.ImplicitOrExplicitKeyword.Text + " " + conversion.Type,
        _ => throw new InvalidOperationException("Unexpected method syntax: " + node.Kind())
    };

    private static string MethodSource(int lines) => "class View\n{\n    void Draw()\n    {\n" +
        string.Join("\n", Enumerable.Repeat("        Work();", lines - 3)) + "\n    }\n}\n";
}
