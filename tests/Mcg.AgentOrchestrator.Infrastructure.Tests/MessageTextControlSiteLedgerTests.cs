using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class MessageTextControlSiteLedgerTests
{
    private const string LedgerPath = "docs/message-text-control-sites.tsv";
    private const string IntentCoordinatorPath =
        "src/Mcg.AgentOrchestrator.App/Orchestration/OperatorIntentCoordinator.cs";
    private const string LedgerHeader = "path\tcount\tclassification\ttyped_record\trequired_fields\tretirement_priority\tnotes";
    private const string DefinitionText =
        "Count each executable C# invocation of Contains, StartsWith, EndsWith or IndexOf whose immediate receiver is named Message. Include qualified or bare member access, conditional access, multiline formatting, parentheses and null-forgiving wrappers. Count all overloads. Exclude comments, string contents, inactive preprocessor regions, aliases and transformed receivers.";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly string[] RequiredFieldNames =
        ["command_identity", "task_attempt_or_generation", "decision", "evidence", "resulting_hold"];

    [Fact]
    public void CandidateSourceTreeStaysWithinLedger()
    {
        var root = VerifiedRepositoryRoot.Find();
        var ledger = ReadLedger(File.ReadAllText(Path.Combine(root, LedgerPath)));
        var scan = ScanMessageCalls(root);

        Assert.NotEmpty(scan);
        Assert.Empty(ledger.Violations.Concat(CompareMessageCounts(scan, ledger.Rows)));
    }

    [Fact]
    public void LedgerRowsAreWellFormed()
    {
        var text = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), LedgerPath));
        var definition = Assert.Single(text.Split('\n').Where(line => line.StartsWith("# definition:", StringComparison.Ordinal)));
        Assert.Equal("# definition: " + DefinitionText, definition.TrimEnd('\r'));

        var ledger = ReadLedger(text);
        Assert.Empty(ledger.Violations);
        Assert.Equal(
            [
                IntentCoordinatorPath,
                "src/Mcg.AgentOrchestrator.App/Orchestration/VerifiedAcceptanceEscalationDecision.cs",
                "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs"
            ],
            ledger.Rows.Where(row => row.Value.RetirementPriority == "first-retirement-target")
                .Select(row => row.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ScanCountsFourCallShapesOnMessageReceivers()
    {
        const string source = """
            _ = entry.Message.Contains("marker");
            _ = Message.StartsWith("marker");
            _ = this.Message.EndsWith("marker");
            _ = (entry.Message!).IndexOf(' ');
            _ = entry.Message?.Contains("marker");
            _ = entry?.Message.StartsWith("marker");
            _ = entry?.Message?.EndsWith("marker");
            _ = (entry?.Message)?.Contains("marker");
            _ = entry.Message?
                .IndexOf("marker", StringComparison.Ordinal);
            _ = entry.\u004dessage.Contains("marker");
            """;

        Assert.Equal(10, CountMessageCalls(ParseMessageSource(source)));
    }

    [Fact]
    public void ScanIgnoresCommentsStringsAliasesAndTransformedReceivers()
    {
        const string source = """
            // entry.Message.Contains("marker");
            var literal = "entry.Message.StartsWith(\"marker\")";
            var alias = entry.Message;
            _ = alias.EndsWith("marker");
            _ = entry.ErrorMessage.IndexOf("marker");
            _ = entry.Message.Trim().Contains("marker");
            _ = entry?.Message.Trim()?.Contains("marker");
            _ = entry.Message.Contains;
            _ = entry.Message?.Wrap(other?.Contains("marker"));
            #if NEVER_DEFINED
            _ = entry.Message.Contains("marker");
            #endif
            """;

        Assert.Equal(0, CountMessageCalls(ParseMessageSource(source)));
    }

    [Fact]
    public void ScanReportsFileWithoutLedgerRow()
    {
        var ledger = ReadLedger(LedgerHeader);
        Assert.Empty(ledger.Violations);
        var scan = new Dictionary<string, int> { [IntentCoordinatorPath] = 1 };

        var violation = Assert.Single(CompareMessageCounts(scan, ledger.Rows));
        Assert.Equal(IntentCoordinatorPath, violation.Path);
        Assert.Equal(ViolationKind.MissingRow, violation.Kind);
    }

    [Fact]
    public void LedgerRowWithoutClassificationIsReported()
    {
        var ledger = ReadLedger($"{LedgerHeader}\n{IntentCoordinatorPath}\t1\t\t-\t-\t-\tExample");
        var violation = Assert.Single(ledger.Violations);

        Assert.Equal(IntentCoordinatorPath, violation.Path);
        Assert.Equal(ViolationKind.MissingClassification, violation.Kind);
    }

    [Fact]
    public void AddedCallInTemporarySourceCopyIsReportedOverLedgerCount()
    {
        var root = VerifiedRepositoryRoot.Find();
        var temporaryRoot = Directory.CreateTempSubdirectory("message-text-ledger-").FullName;

        try
        {
            var copy = Path.Combine(temporaryRoot, IntentCoordinatorPath);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(Path.Combine(root, IntentCoordinatorPath), copy);
            var baseline = ScanMessageCalls(temporaryRoot);
            // Give the copy its own ceiling so the negative control stays valid as production calls retire.
            var ceiling = baseline.GetValueOrDefault(IntentCoordinatorPath);
            var ledger = ReadLedger(
                $"{LedgerHeader}\n{IntentCoordinatorPath}\t{ceiling}\tdisplay-only\t-\t-\t-\tTemporary fixture");
            Assert.Empty(ledger.Violations);
            Assert.Empty(CompareMessageCounts(baseline, ledger.Rows));

            File.AppendAllText(copy, """

                internal static class MessageTextLedgerProbe
                {
                    internal static bool Matches(Mcg.AgentOrchestrator.Core.ProgressEvent entry) =>
                        entry.Message.Contains("message-text ledger probe", System.StringComparison.Ordinal);
                }

                """);
            Assert.Empty(ParseMessageSource(File.ReadAllText(copy)).GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            var scan = ScanMessageCalls(temporaryRoot);
            Assert.Equal(ceiling + 1, scan[IntentCoordinatorPath]);
            var violation = Assert.Single(CompareMessageCounts(scan, ledger.Rows));
            Assert.Equal(IntentCoordinatorPath, violation.Path);
            Assert.Equal(ViolationKind.OverCount, violation.Kind);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static IReadOnlyDictionary<string, int> ScanMessageCalls(string root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var source = Path.Combine(root, "src");
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (SourceGuardFileFilter.BuildOutputAndScratch.Excludes(path))
                continue;

            var count = CountMessageCalls(ParseMessageSource(File.ReadAllText(file)));
            if (count > 0)
                counts.Add(path, count);
        }

        return counts;
    }

    private static SyntaxTree ParseMessageSource(string source) => CSharpSyntaxTree.ParseText(source, ParseOptions);

    private static int CountMessageCalls(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Count(IsMessageTextCall);

    private static bool IsMessageTextCall(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null
        };
        if (name is not ("Contains" or "StartsWith" or "EndsWith" or "IndexOf"))
            return false;

        // A member binding gets its receiver from the conditional access whose right side contains the call.
        var receiver = invocation.Expression is MemberAccessExpressionSyntax member
            ? member.Expression
            : invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                .FirstOrDefault(access => access.WhenNotNull.Span.Contains(invocation.Span))?.Expression;

        while (true)
        {
            switch (receiver)
            {
                case ParenthesizedExpressionSyntax parentheses:
                    receiver = parentheses.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    receiver = postfix.Operand;
                    continue;
                case ConditionalAccessExpressionSyntax conditional:
                    receiver = conditional.WhenNotNull;
                    continue;
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText == "Message";
                case MemberAccessExpressionSyntax access:
                    return access.Name.Identifier.ValueText == "Message";
                case MemberBindingExpressionSyntax binding:
                    return binding.Name.Identifier.ValueText == "Message";
                default:
                    return false;
            }
        }
    }

    private static Ledger ReadLedger(string text)
    {
        var rows = new Dictionary<string, LedgerRow>(StringComparer.Ordinal);
        var violations = new List<Violation>();
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')).ToArray();
        if (lines.FirstOrDefault() != LedgerHeader)
            violations.Add(new(LedgerPath, ViolationKind.MalformedRow, "Expected the seven-column ledger header."));

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var columns = line.Split('\t');
            var path = columns[0];
            if (columns.Length != 7 || !path.StartsWith("src/", StringComparison.Ordinal) ||
                !path.EndsWith(".cs", StringComparison.Ordinal) || path.Contains('\\') ||
                path.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                violations.Add(new(path, ViolationKind.MalformedRow, "Expected seven columns and a src-relative C# path."));
                continue;
            }

            if (!seenPaths.Add(path))
            {
                violations.Add(new(path, ViolationKind.DuplicateRow, "Expected one ledger row per source file."));
                continue;
            }

            if (!int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                violations.Add(new(path, ViolationKind.MalformedCount, $"Invalid ceiling '{columns[1]}'."));
                continue;
            }

            switch (columns[2])
            {
                case "control":
                    CheckControlPayload(path, columns[3], columns[4], violations);
                    break;
                case "display-only":
                    break;
                default:
                    var kind = string.IsNullOrWhiteSpace(columns[2])
                        ? ViolationKind.MissingClassification
                        : ViolationKind.InvalidClassification;
                    violations.Add(new(path, kind, "Expected control or display-only."));
                    continue;
            }

            rows.Add(path, new(count, columns[5]));
        }

        return new(rows, violations);
    }

    private static void CheckControlPayload(string path, string records, string fields, List<Violation> violations)
    {
        if (!records.Split(',', StringSplitOptions.TrimEntries).All(SyntaxFacts.IsValidIdentifier))
            violations.Add(new(path, ViolationKind.ControlRowMissingTypedRecord, "Name each typed replacement record."));

        var entries = fields.Split(';', StringSplitOptions.TrimEntries)
            .Select(field => field.Split('=', 2, StringSplitOptions.TrimEntries)).ToArray();
        foreach (var name in RequiredFieldNames)
        {
            if (entries.Count(entry => entry.Length == 2 && entry[0] == name &&
                    entry[1].Length > 0 && entry[1] is not ("-" or "n/a" or "n/a ()")) != 1)
                violations.Add(new(path, ViolationKind.ControlRowMissingField, $"Describe {name} or give a reason it does not apply."));
        }
    }

    private static IEnumerable<Violation> CompareMessageCounts(
        IReadOnlyDictionary<string, int> scan, IReadOnlyDictionary<string, LedgerRow> rows)
    {
        foreach (var (path, actual) in scan)
        {
            if (!rows.TryGetValue(path, out var row))
                yield return new(path, ViolationKind.MissingRow, $"Found {actual} qualifying calls with no ledger row.");
            else if (actual > row.Count)
                yield return new(path, ViolationKind.OverCount, $"Found {actual} qualifying calls above the ledger ceiling of {row.Count}.");
        }
    }

    private sealed record Ledger(IReadOnlyDictionary<string, LedgerRow> Rows, IReadOnlyList<Violation> Violations);
    private sealed record LedgerRow(int Count, string RetirementPriority);
    private sealed record Violation(string Path, ViolationKind Kind, string Detail);

    private enum ViolationKind
    {
        MalformedRow,
        MalformedCount,
        DuplicateRow,
        MissingClassification,
        InvalidClassification,
        ControlRowMissingTypedRecord,
        ControlRowMissingField,
        MissingRow,
        OverCount
    }
}
