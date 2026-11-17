using System.IO.Enumeration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Attribution needs positive dependency evidence, never blanket gate selection.
internal static class MergeTrainClassReachAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(MergeTrainReceipt receipt, string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members, out IReadOnlyList<string> failingClasses)
    {
        failingClasses = [];
        if (receipt.Outcome != MergeTrainGateOutcome.Failed ||
            receipt.FailedChecks.Contains(SourceSizeRatchetPreflight.CheckName, StringComparer.Ordinal)) return null;
        var classes = MergeTrainReachAttribution.ReadFailingClasses(receipt.GateTestResultPaths,
            MergeTrainRedAttribution.ReadConfirmedIdentities(receipt));
        if (classes is null) return null;
        try
        {
            var resolver = new AcceptanceTestSourceResolver.Request(workspacePath);
            var declarations = classes.ToDictionary(name => name,
                name => resolver.ResolveIdentity(name + ".Failure"), StringComparer.Ordinal);
            if (declarations.Values.Any(paths => paths.Count == 0)) return null;
            var readers = declarations.ToDictionary(pair => pair.Key,
                pair => pair.Value.Select(path => resolver.ReadSyntax(path)).ToArray(), StringComparer.Ordinal);
            if (readers.Values.Any(sources => sources.Any(source => source is null))) return null;
            var candidates = new List<MergeTrainMemberBinding>();
            var unknown = false;
            foreach (var member in members)
            {
                var paths = member.LandingPaths.Select(path => Normalize(workspacePath, path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var sources = paths.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
                var reach = sources.Length == 0 ? new HashSet<string>(StringComparer.Ordinal)
                    : RepositoryTestReach.TryReadReachableTestClasses(workspacePath, sources);
                // Unresolved members cannot be excluded from the common-member set.
                if (reach is null) { unknown = true; continue; }
                if (classes.All(name => reach.Contains(name) || paths.Intersect(declarations[name],
                        StringComparer.OrdinalIgnoreCase).Any() || readers[name].Any(source =>
                        ReadsChangedPath(source!, workspacePath, paths))))
                    candidates.Add(member);
            }
            if (unknown || candidates.Count != 1) return null;
            failingClasses = classes;
            return candidates[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool ReadsChangedPath(CompilationUnitSyntax source, string root, IReadOnlyList<string> paths)
    {
        // The C# reach map indexes identifiers, not files read as data. Supplement it
        // only for sources that actually perform file reads or directory enumeration.
        if (!source.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                call.Expression is MemberAccessExpressionSyntax access &&
                ((access.Expression.ToString() is "File" or "System.IO.File" &&
                    access.Name.Identifier.ValueText.StartsWith("Read", StringComparison.Ordinal)) ||
                 (access.Expression.ToString() is "Directory" or "System.IO.Directory" &&
                    access.Name.Identifier.ValueText is "EnumerateFiles" or "GetFiles") ||
                 (access.Expression.ToString() is "XDocument" or "XElement" &&
                    access.Name.Identifier.ValueText == "Load"))) &&
            !source.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(creation =>
                creation.Type.ToString() is "StreamReader" or "System.IO.StreamReader")) return false;
        var literals = source.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(literal => literal.Token.ValueText.Replace('\\', '/').TrimEnd('/'))
            .Where(value => value.Length > 0).ToArray();
        var directories = literals.Where(value => !Path.IsPathRooted(value) &&
            !value.Contains('*') && !value.Contains('?') && Directory.Exists(Path.Combine(root, value))).ToArray();
        return paths.Any(path => literals.Any(value => string.Equals(path, value, StringComparison.OrdinalIgnoreCase)) ||
            ((directories.Length == 0 || directories.Any(directory => path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))) &&
             literals.Any(value => !value.Contains('/') &&
                 FileSystemName.MatchesSimpleExpression(value, Path.GetFileName(path), ignoreCase: true))));
    }

    private static string Normalize(string root, string path) =>
        Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, path))).Replace('\\', '/');

    internal static string FormatInconclusive(string outerAttemptId, MergeTrainReceipt receipt, string? sweepReason)
    {
        var failing = MergeTrainFocusedRecheckDecision.TryReadFailingIdentities(receipt, out var identities)
            ? identities : MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)
                .Select(failure => failure.TestName).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        return $"PHASE_PROGRESS phase=train-inconclusive outer_attempt={outerAttemptId} train_identity={receipt.Identity.Value} " +
            $"receipt={receipt.ReceiptId} scope=merge-train rule=inconclusive reason=inconclusive sweep={sweepReason ?? "skipped"} " +
            $"members={string.Join(',', receipt.Identity.Members.Select(member => member.GoalId.Value))} " +
            $"failing_tests={string.Join(',', failing)}";
    }
}
