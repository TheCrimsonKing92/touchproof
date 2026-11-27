using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Candidate-local composition shared by replay and live acceptance. Replay owns its cache policy.
internal static class AcceptanceImpactSelectionReader
{
    internal static AcceptanceSelectedExecutionInput ReadLive(string checkout, IReadOnlyList<string> changedFiles,
        AcceptanceGateEngineSettings? engineSettings = null, string? integrationBranch = null)
    {
        var settings = engineSettings ?? AcceptanceGateEngineSettings.Load(checkout);
        var routing = new ImpactReplayChangedSourceRouting(checkout);
        IReadOnlyList<AcceptanceTestClassDescriptor> classes;
        try { classes = AcceptanceTestClassSourceScanner.Scan(checkout); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            string Relative(string path) => Path.GetRelativePath(checkout, Path.GetFullPath(path, checkout)).Replace('\\', '/');
            var inventoryFailure = $"{error.GetType().Name}: {error.Message}";
            var support = ImpactReplayChangedSupportSelection.Incomplete(
                changedFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && routing.IsTestSource(path))
                    .Select(Relative).Distinct(StringComparer.OrdinalIgnoreCase),
                routing.TestProjects.Select(project => Relative(project.Path)).Order(StringComparer.Ordinal).ToArray(),
                inventoryFailure, settings.SupportClosureClassThreshold);
            return new(ReverseDependencyTestSelection.Unreadable(inventoryFailure), [], [], SupportSelections: support.SupportSelections);
        }
        var sources = new ImpactReplayTestSourceIndex(LaneTestSourceRootResolver.SourceFiles(checkout,
            LaneTestSourceRootResolver.Resolve(checkout)), routing.TestProjects, checkout);
        var guards = SourceReadingGuardTestClasses.Read(sources, classes, out var scanFiles);
        var (changedLineRanges, scanChanges) = ReadCandidateDiff(checkout, integrationBranch, changedFiles, scanFiles.Count > 0);
        var impact = Read(checkout, changedFiles, routing, sources, classes, guards,
            ChangedFileTestClassPathMapper.TestProjectDirectories(settings), bypassCache: false,
            changedLineRanges: changedLineRanges, scanFiles: scanFiles, scanChanges: scanChanges,
            supportClosureThreshold: settings.SupportClosureClassThreshold, scopeChangedTestsByProject: true);
        var selection = impact.Selection.Outcome == ReverseDependencySelectionOutcome.Resolved &&
            impact.ChangedTests.AbandonReason is { } reason
            ? ReverseDependencyTestSelection.Abandoned(ReverseDependencyDegradationKind.ChangedFileUnmapped, reason)
            : impact.Selection;
        return new(selection, classes, (guards?.Classes ?? []).Concat(impact.DeclaredGuards.Selections
            .Select(item => item.ClassName)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ProjectClasses: project => routing.TestProjects.FirstOrDefault(item => item.Path.Equals(
                Path.GetFullPath(project, checkout), StringComparison.OrdinalIgnoreCase)) is { } match
                ? AcceptanceTestClassSourceScanner.Scan(match.Path, match.Sources.ToArray()) : [],
            AssemblyScanReasons: impact.AssemblyScans,
            SupportSelections: impact.ChangedTests.Support.SupportSelections,
            SupportProjectClasses: impact.ChangedTests.Support.ProjectClasses);
    }

    internal static (ReverseDependencyTestSelection Selection, ImpactReplayDeclaredGuardReport DeclaredGuards,
        (IReadOnlyList<string> Classes, bool Gained, string? AbandonReason, IReadOnlyList<string> OtherFiles,
            IReadOnlyList<string> QualifiedClasses, ImpactReplayChangedSupportSelection Support) ChangedTests,
        IReadOnlyList<AssemblyScanningGuardTestClasses.SelectionReason> AssemblyScans) Read(
        string checkout, IReadOnlyList<string> changedFiles, ImpactReplayChangedSourceRouting routing,
        ImpactReplayTestSourceIndex sources, IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        ImpactReplaySourceGuardReport? guards, IReadOnlyList<string> testProjectDirectories, bool bypassCache,
        IReadOnlyDictionary<string, IReadOnlyList<(int StartLine, int EndLine)>>? changedLineRanges = null,
        IReadOnlyList<AssemblyScanningGuardTestClasses.ScanFile>? scanFiles = null,
        IReadOnlyList<string>? scanChanges = null,
        int supportClosureThreshold = ImpactReplayChangedSupportReferenceScan.DefaultClosureThreshold,
        bool scopeChangedTestsByProject = false)
    {
        var changedSources = changedFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var changedTests = changedSources.Where(routing.IsTestSource).ToArray();
        var productionSources = changedSources.Except(changedTests, StringComparer.OrdinalIgnoreCase).ToArray();
        var selection = productionSources.Length == 0 && changedTests.Length > 0
            ? ReverseDependencyTestSelection.Resolved([])
            : ReverseDependencyTestImpactReader.Read(checkout, productionSources, bypassCache: bypassCache,
                propagateShippingSourceReach: true, changedLineRanges: changedLineRanges);
        var augmented = ImpactReplayChangedTestClassSelection.Read(checkout, changedTests, selection.TestClassNames, routing,
            supportClosureThreshold, sources: sources);
        selection = ImpactReplaySelectionAugmentation.Apply(selection, changedFiles, sources, classes, guards,
            out var declaredGuards, scopeChangedTestsByProject ? null : augmented.QualifiedClasses,
            testProjectDirectories, out var assemblyScans, scanFiles, scanChanges);
        return (selection, declaredGuards, augmented, assemblyScans);
    }

    private static (IReadOnlyDictionary<string, IReadOnlyList<(int StartLine, int EndLine)>>? LineRanges,
        IReadOnlyList<string> ScanChanges) ReadCandidateDiff(
        string checkout, string? integrationBranch, IReadOnlyList<string> changedFiles, bool hasScanSites)
    {
        if (string.IsNullOrWhiteSpace(integrationBranch)) return (null, changedFiles);
        var mergeBase = GoalAcceptanceVerifier.ResolveGitScalar(checkout, "merge-base", "HEAD", integrationBranch);
        if (mergeBase is null) return (null, changedFiles);
        try
        {
            var diff = GitCli.Run(checkout, GitCli.DefaultTimeoutMilliseconds, "-c", "core.quotePath=false",
                "diff", "--unified=0", "--inter-hunk-context=0", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames",
                "--src-prefix=a/", "--dst-prefix=b/", mergeBase, "--", "*.cs");
            if (diff.ExitCode != 0 || diff.DrainTimedOut) return (null, changedFiles);
            var ranges = ZeroContextDiffLineRangeParser.Parse(diff.Output);
            var scanChanges = hasScanSites && (diff.Output.Contains("\nnew file mode ", StringComparison.Ordinal) ||
                diff.Output.Contains("\ndeleted file mode ", StringComparison.Ordinal))
                ? ReadRenameCounterparts(checkout, mergeBase, changedFiles) : changedFiles;
            return (ranges, scanChanges);
        }
        catch (Exception)
        {
            // Precision is optional; missing or unparseable Git output preserves path-only selection.
            return (null, changedFiles);
        }
    }

    private static IReadOnlyList<string> ReadRenameCounterparts(string checkout, string mergeBase, IReadOnlyList<string> changedFiles)
    {
        GitCli.GitResult status;
        try
        {
            status = GitCli.Run(checkout, GitCli.DefaultTimeoutMilliseconds, "diff", "--name-status", "-z",
                "--find-renames", "--diff-filter=R", mergeBase, "--");
        }
        catch (Exception)
        {
            return changedFiles;
        }
        if (!status.Succeeded || status.DrainTimedOut) return changedFiles;
        var fields = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length % 3 != 0) return changedFiles;
        var original = changedFiles.Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(original, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fields.Length; index += 3)
        {
            var before = fields[index + 1];
            var after = fields[index + 2];
            if (original.Contains(before) || original.Contains(after))
            {
                paths.Add(before);
                paths.Add(after);
            }
        }
        return paths.ToArray();
    }
}
