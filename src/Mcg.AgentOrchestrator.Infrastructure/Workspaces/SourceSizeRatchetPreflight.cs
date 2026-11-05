using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SourceSizeRatchetPreflight
{
    internal const string CheckName = "source size ratchet preflight";

    internal static IReadOnlyList<SourceClassCeiling> TryReadClassAuthority(string worktreeRoot) =>
        ReadAuthority(worktreeRoot)?.Classes ?? [];

    internal static IReadOnlyList<SourceSizeCeiling>? TryReadAuthority(string worktreeRoot) =>
        ReadAuthority(worktreeRoot) is { Files.Count: > 0 } authority ? authority.Files : null;

    // Read membership and values together: comments are not rows, and unsupported
    // expressions make the authority incomplete instead of silently dropping a limit.
    private static Authority? ReadAuthority(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        try
        {
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, SourceSizeRatchet.SourcePath))).GetRoot();
            var files = new List<SourceSizeCeiling>();
            var classes = new List<SourceClassCeiling>();
            var complete = true;
            foreach (var row in syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var type = row.Type.ToString().Split('.').Last();
                if (type is not (nameof(SourceSizeCeiling) or nameof(SourceClassCeiling))) continue;
                var arguments = row.ArgumentList?.Arguments.Select(a => a.Expression).ToArray() ?? [];
                var values = arguments.OfType<LiteralExpressionSyntax>().Select(a => a.Token.Value).ToArray();
                if (values.Length != arguments.Length || values.Length != (type == nameof(SourceSizeCeiling) ? 2 : 3) ||
                    values[0] is not string name || values[1] is not int maximum ||
                    (values.Length == 3 && values[2] is not int))
                {
                    complete = false;
                    continue;
                }
                if (type == nameof(SourceSizeCeiling)) files.Add(new(name, maximum));
                else classes.Add(new(name, maximum, (int)values[2]!));
            }
            return new(files, classes, complete);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static SourceSizeRatchetPreflightResult Evaluate(
        string worktreeRoot, Func<string, IEnumerable<string>>? readLines = null)
    {
        var authority = ReadAuthority(worktreeRoot);
        if (authority is null || authority.Files.Count == 0)
            return SourceSizeRatchetPreflightResult.Declined;

        var violations = SourceSizeRatchet.Evaluate(worktreeRoot, authority.Files, readLines)
            .Concat(SourceSizeRatchet.EvaluateClasses(worktreeRoot, authority.Classes, readLines)).ToArray();
        var blocking = violations.Where(v => v.Kind != SourceSizeViolationKind.Unavailable).ToArray();
        return new(blocking.Length > 0, string.Join(Environment.NewLine, blocking.Select(v => v.Message)), violations,
            EvaluationComplete: authority.Complete && violations.All(v => v.Kind != SourceSizeViolationKind.Unavailable));
    }

    internal static IReadOnlyList<string> BlockingViolationMessages(SourceSizeRatchetPreflightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Violations.Where(v => v.Kind != SourceSizeViolationKind.Unavailable).Select(v => v.Message).ToArray();
    }

    private sealed record Authority(IReadOnlyList<SourceSizeCeiling> Files, IReadOnlyList<SourceClassCeiling> Classes, bool Complete);
}

internal sealed record SourceSizeRatchetPreflightResult(
    bool HasBlockingViolation, string Message, IReadOnlyList<SourceSizeViolation> Violations,
    bool EvaluationComplete = true, bool AuthorityAvailable = true)
{
    internal static SourceSizeRatchetPreflightResult Declined { get; } = new(false, string.Empty, [], false, false);
}
