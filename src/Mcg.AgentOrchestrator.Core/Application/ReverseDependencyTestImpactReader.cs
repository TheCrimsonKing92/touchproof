using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Core;

internal static class ReverseDependencyTestImpactReader
{
    // Home planner routing guards this path; foreign selected execution fails closed on absence via Read and AcceptanceSelectedExecutionPlan.Create.
    private const string InfrastructureTestProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const int MaximumDependencyHops = 2;
    // The Infrastructure test closure reached 4,012 tracked source files on 2026-10-18. Keep
    // bounded growth headroom; exceeding this cap abandons selection for the full suite.
    internal const int MaximumIndexedSourceFiles = 8_000;
    private const int MaximumRetainedSnapshots = 4;
    private const string CacheSchema = "reverse-dependency-index-v1";
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CachedIndex>> CacheByFingerprint =
        new(StringComparer.Ordinal);
    private static readonly LinkedList<CachedIndex> CacheRecency = new();

    internal static ReverseDependencyTestSelection Read(
        string repositoryRoot,
        IReadOnlyList<string> changedSourcePaths,
        bool bypassCache = false,
        int maximumIndexedSourceFiles = MaximumIndexedSourceFiles,
        bool propagateShippingSourceReach = false,
        Action<string, string>? shippingReachDiagnostics = null,
        SelectionSourceTexts? sourceTexts = null,
        IReadOnlyList<string>? additionalTargetTestProjects = null,
        IReadOnlyDictionary<string, IReadOnlyList<(int StartLine, int EndLine)>>? changedLineRanges = null)
    {
        var headroom = new RepositoryTestImpactHeadroom(
            null, maximumIndexedSourceFiles, null,
            null);
        if (changedSourcePaths.Count == 0)
        {
            return ReverseDependencyTestSelection.Abandoned(
                ReverseDependencyDegradationKind.ChangedSourceFileCount,
                "Reverse-dependency selection requires at least one changed source file.",
                headroom: headroom);
        }

        try
        {
            repositoryRoot = Path.GetFullPath(repositoryRoot);
            var testProjectPath = ResolveWithinRepository(repositoryRoot, InfrastructureTestProject);
            if (testProjectPath is null)
            {
                return ReverseDependencyTestSelection.Unreadable(
                    $"The dependent test project could not be read: {InfrastructureTestProject}", headroom: headroom);
            }

            if (!File.Exists(testProjectPath))
            {
                return IsDeliberatelyPartialRepositoryRoot(repositoryRoot)
                    ? ReverseDependencyTestSelection.Unavailable with { Headroom = headroom }
                    : ReverseDependencyTestSelection.Unreadable(
                        $"The dependent test project could not be read: {InfrastructureTestProject}", headroom: headroom);
            }

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { testProjectPath };
            foreach (var project in additionalTargetTestProjects ?? [])
            {
                var target = ResolveWithinRepository(repositoryRoot, project);
                if (target is null || !File.Exists(target))
                    return ReverseDependencyTestSelection.Unreadable(
                        $"The dependent test project could not be read: {project}", headroom: headroom);
                targets.Add(target);
            }
            var closure = ReadIndexedSourceClosure(repositoryRoot, testProjectPath, out var projectFailure, targets);
            if (closure is null)
            {
                return ReverseDependencyTestSelection.Unreadable(projectFailure!, headroom: headroom);
            }

            var (projects, sourceOwnership) = closure.Value;
            headroom = headroom with { IndexedSourceFileCount = sourceOwnership.Count };
            if (sourceOwnership.Count > maximumIndexedSourceFiles)
            {
                return ReverseDependencyTestSelection.Abandoned(
                    ReverseDependencyDegradationKind.IndexedSourceBound,
                    $"Reverse-dependency indexing exceeded the {maximumIndexedSourceFiles}-source-file bound.",
                    headroom: headroom);
            }

            var snapshot = ReadSnapshot(
                repositoryRoot,
                projects,
                sourceOwnership,
                out var snapshotFailure, sourceTexts, targets);
            if (snapshot is null)
            {
                return ReverseDependencyTestSelection.Unreadable(snapshotFailure!, headroom: headroom);
            }

            var disposition = bypassCache
                ? ReverseDependencyCacheDisposition.Bypass
                : ReverseDependencyCacheDisposition.Miss;
            var reparsedFileCount = 0;
            ReverseDependencyIndex index;
            if (!bypassCache && TryGetCachedIndex(snapshot.Fingerprint, out var cachedIndex, out var hitCount))
            {
                index = cachedIndex;
                disposition = ReverseDependencyCacheDisposition.Hit;
                var hitReceipt = CreateReceipt(snapshot, disposition, reparsedFileCount, hitCount);
                return Select(index, changedSourcePaths, repositoryRoot, hitReceipt, headroom, propagateShippingSourceReach, shippingReachDiagnostics, changedLineRanges);
            }

            index = ParseSnapshot(snapshot, repositoryRoot, out var parseFailure, out reparsedFileCount)!;
            if (index is null)
            {
                var failedReceipt = CreateReceipt(
                    snapshot,
                    disposition,
                    reparsedFileCount,
                    RetainedSnapshotCount());
                return ReverseDependencyTestSelection.Unreadable(parseFailure!, failedReceipt, headroom);
            }

            var retainedSnapshotCount = bypassCache
                ? RetainedSnapshotCount()
                : RetainIndex(snapshot.Fingerprint, index);
            var receipt = CreateReceipt(snapshot, disposition, reparsedFileCount, retainedSnapshotCount);
            return Select(index, changedSourcePaths, repositoryRoot, receipt, headroom, propagateShippingSourceReach, shippingReachDiagnostics, changedLineRanges);
        }
        catch (IOException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency source I/O failed: {exception.Message}", headroom: headroom);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency source access failed: {exception.Message}", headroom: headroom);
        }
        catch (System.Xml.XmlException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency project graph is malformed: {exception.Message}", headroom: headroom);
        }
        catch (ArgumentException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency path evidence is malformed: {exception.Message}", headroom: headroom);
        }
        catch (NotSupportedException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency path evidence is unsupported: {exception.Message}", headroom: headroom);
        }
        catch (Exception exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency evidence failed: {exception.Message}", headroom: headroom);
        }
    }

    private static ReverseDependencyTestSelection Select(
        ReverseDependencyIndex index,
        IReadOnlyList<string> changedSourcePaths,
        string repositoryRoot,
        ReverseDependencyCacheReceipt receipt,
        RepositoryTestImpactHeadroom headroom,
        bool propagateShippingSourceReach,
        Action<string, string>? shippingReachDiagnostics,
        IReadOnlyDictionary<string, IReadOnlyList<(int StartLine, int EndLine)>>? changedLineRanges)
    {
        var indexedFiles = index.Files;
        var knownMembersByType = index.KnownMembersByType;
        try
        {
            var normalizedChangedPaths = changedSourcePaths
                .Select(NormalizeRepositoryPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var changedPaths = new List<string>();
            foreach (var changedSourcePath in normalizedChangedPaths)
            {
                var fullPath = ResolveWithinRepository(repositoryRoot, changedSourcePath);
                if (fullPath is null)
                {
                    return ReverseDependencyTestSelection.Unreadable(
                        $"Changed source is outside the dependent project graph or unreadable: {changedSourcePath}",
                        receipt, headroom);
                }

                if (!indexedFiles.ContainsKey(fullPath))
                {
                    if (File.Exists(fullPath))
                    {
                        return ReverseDependencyTestSelection.Unreadable(
                            $"Changed source is outside the dependent project graph or unreadable: {changedSourcePath}",
                            receipt, headroom);
                    }

                    continue;
                }

                changedPaths.Add(fullPath);
            }

            if (changedPaths.Count == 0)
            {
                return ReverseDependencyTestSelection.Unavailable with { CacheReceipt = receipt, Headroom = headroom };
            }

            var normalizedRanges = new Dictionary<string, List<(int StartLine, int EndLine)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, ranges) in changedLineRanges ?? new Dictionary<string, IReadOnlyList<(int, int)>>())
            {
                var normalized = NormalizeRepositoryPath(path);
                if (!normalizedRanges.TryGetValue(normalized, out var merged))
                    normalizedRanges.Add(normalized, merged = []);
                merged.AddRange(ranges.Where(range => range.StartLine >= 1 && range.EndLine >= range.StartLine));
            }
            var frontier = MergeFrontier(changedPaths
                .SelectMany(path =>
                {
                    var file = indexedFiles[path];
                    var declarations = file.Symbols!.DeclaredTypes;
                    var key = NormalizeRepositoryPath(Path.GetRelativePath(repositoryRoot, path));
                    if (normalizedRanges.TryGetValue(key, out var ranges))
                    {
                        var overlapping = declarations.Where(declaration =>
                            OverlapsAnyRange(declaration.StartLine, declaration.EndLine, ranges)).ToArray();
                        if (overlapping.Length > 0)
                        {
                            declarations = overlapping;
                        }
                    }

                    return declarations.Select(declaration => new FrontierSymbol(
                        declaration.Name,
                        TestSelectionMode.AllReferencingClasses,
                        SeedFilter(declaration, ranges, isShippingFile: !file.IsTargetTestProject)));
                }));
            headroom = headroom with { LargestFrontierSymbolCount = frontier.Length };
            if (frontier.Length == 0)
            {
                return ReverseDependencyTestSelection.Unreadable(
                    "Changed source declared no top-level type for reverse-dependency selection.",
                    receipt, headroom);
            }

            frontier = MergeFrontier(frontier
                .Concat(WideImpactTestSelectionInputs.GetSeedTypeNames(normalizedChangedPaths)
                    .Select(name => new FrontierSymbol(name, TestSelectionMode.AllReferencingClasses))));

            var seenPaths = changedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedTestClasses = new SortedSet<string>(StringComparer.Ordinal);
            HashSet<string>? shippingFrontier = null;
            for (var hop = 1; hop <= MaximumDependencyHops; hop++)
            {
                headroom = headroom with
                {
                    LargestFrontierSymbolCount = Math.Max(headroom.LargestFrontierSymbolCount!.Value, frontier.Length)
                };

                var frontierSet = frontier
                    .Select(symbol => symbol.Name)
                    .ToHashSet(StringComparer.Ordinal);
                var hasMemberFilters = frontier.Any(symbol => symbol.MemberFilter is not null);
                bool ReferencesFrontier(CSharpTestClassScanner.SourceTypeDeclaration declaration) =>
                    declaration.ReferencedIdentifiers.Overlaps(frontierSet) &&
                    (!hasMemberFilters || frontier.Any(symbol => References(declaration, symbol, knownMembersByType)));

                var consumers = indexedFiles.Values
                    .Where(file => !seenPaths.Contains(file.Path))
                    .Where(file => file.Symbols!.DeclaredTypes.Any(ReferencesFrontier))
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .ToArray();
                foreach (var consumer in consumers.Where(file => file.IsTargetTestProject))
                {
                    var referencedFrontier = frontier
                        .Where(symbol => consumer.Symbols!.DeclaredTypes.Any(declaration =>
                            References(declaration, symbol, knownMembersByType)))
                        .ToArray();
                    var allReferencingSymbols = referencedFrontier
                        .Where(symbol => symbol.TestSelectionMode == TestSelectionMode.AllReferencingClasses &&
                                         symbol.MemberFilter is null)
                        .Select(symbol => symbol.Name)
                        .ToHashSet(StringComparer.Ordinal);
                    var filteredReferencingSymbols = referencedFrontier
                        .Where(symbol => symbol.TestSelectionMode == TestSelectionMode.AllReferencingClasses &&
                                         symbol.MemberFilter is not null)
                        .ToArray();
                    var ownedClassNames = referencedFrontier
                        .Where(symbol => symbol.TestSelectionMode == TestSelectionMode.OwnedClassOnly)
                        .Select(symbol => $"{symbol.Name}Tests")
                        .ToHashSet(StringComparer.Ordinal);
                    selectedTestClasses.UnionWith(
                        consumer.Symbols!.DeclaredTypes
                            .Where(declaration => consumer.Symbols.TestClassNames.Contains(
                                declaration.Name,
                                StringComparer.Ordinal))
                            .Where(declaration =>
                                ownedClassNames.Contains(declaration.Name) ||
                                declaration.ReferencedIdentifiers.Overlaps(allReferencingSymbols) ||
                                filteredReferencingSymbols.Any(symbol => References(declaration, symbol, knownMembersByType)))
                            .Select(declaration => declaration.Name));
                    if (shippingReachDiagnostics is not null && hop == 2)
                    {
                        foreach (var declaration in consumer.Symbols!.DeclaredTypes.Where(declaration =>
                                     consumer.Symbols.TestClassNames.Contains(declaration.Name, StringComparer.Ordinal)))
                        foreach (var symbol in referencedFrontier.Where(symbol => shippingFrontier!.Contains(symbol.Name)))
                        {
                            if (symbol.TestSelectionMode == TestSelectionMode.AllReferencingClasses
                                    ? References(declaration, symbol, knownMembersByType)
                                    : declaration.Name == $"{symbol.Name}Tests")
                                shippingReachDiagnostics(symbol.Name, declaration.Name);
                        }
                    }
                }

                if (hop == MaximumDependencyHops)
                    break;

                foreach (var consumer in consumers)
                {
                    seenPaths.Add(consumer.Path);
                }

                HashSet<string>? shippingConsumersWithMultipleParts = null;
                if (propagateShippingSourceReach || shippingReachDiagnostics is not null)
                {
                    var shippingConsumers = consumers.Where(file => !file.IsTargetTestProject)
                        .SelectMany(file => file.Symbols!.DeclaredTypes)
                        .Where(ReferencesFrontier)
                        .ToArray();
                    shippingFrontier = shippingConsumers
                        .Select(declaration => declaration.Name)
                        .ToHashSet(StringComparer.Ordinal);
                    if (propagateShippingSourceReach)
                    {
                        shippingConsumersWithMultipleParts = shippingConsumers
                            .Where(declaration => declaration.IsPartial)
                            .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
                            .Where(parts => parts.Count() > 1)
                            .Select(parts => parts.Key)
                            .ToHashSet(StringComparer.Ordinal);
                    }
                }

                HashSet<string>? helperPeers = null;
                HashSet<string>? helpersWithMultipleParts = null;
                if (propagateShippingSourceReach)
                {
                    var helperConsumers = consumers.Where(file => file.IsTargetTestProject)
                        .SelectMany(file => file.Symbols!.DeclaredTypes.Where(declaration =>
                            ReferencesFrontier(declaration) &&
                            !file.Symbols.TestClassNames.Contains(declaration.Name, StringComparer.Ordinal)))
                        .ToArray();
                    helperPeers = helperConsumers.Select(declaration => declaration.Name)
                        .ToHashSet(StringComparer.Ordinal);
                    helperPeers.UnionWith(shippingFrontier!);
                    helpersWithMultipleParts = helperConsumers
                        .Where(declaration => declaration.IsPartial)
                        .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
                        .Where(parts => parts.Count() > 1)
                        .Select(parts => parts.Key)
                        .ToHashSet(StringComparer.Ordinal);
                }

                frontier = MergeFrontier(consumers
                    .SelectMany(file => file.Symbols!.DeclaredTypes
                        .Where(ReferencesFrontier)
                        .Where(declaration =>
                            !file.IsTargetTestProject ||
                            !file.Symbols.TestClassNames.Contains(declaration.Name, StringComparer.Ordinal))
                        .Select(declaration => new FrontierSymbol(
                            declaration.Name,
                            file.IsTargetTestProject || propagateShippingSourceReach
                                ? TestSelectionMode.AllReferencingClasses
                                : TestSelectionMode.OwnedClassOnly,
                            ConsumerFilter(declaration, frontier, knownMembersByType,
                                file.IsTargetTestProject ? helperPeers : propagateShippingSourceReach ? shippingFrontier : null,
                                (file.IsTargetTestProject ? helpersWithMultipleParts : shippingConsumersWithMultipleParts)
                                    ?.Contains(declaration.Name) == true)))));
                if (frontier.Length == 0)
                {
                    break;
                }
            }

            return ReverseDependencyTestSelection.Resolved(selectedTestClasses, receipt,
                headroom with { SelectedTestClassCount = selectedTestClasses.Count });
        }
        catch (IOException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency source I/O failed: {exception.Message}", headroom: headroom);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency source access failed: {exception.Message}", headroom: headroom);
        }
        catch (System.Xml.XmlException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency project graph is malformed: {exception.Message}", headroom: headroom);
        }
        catch (ArgumentException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency path evidence is malformed: {exception.Message}", headroom: headroom);
        }
        catch (NotSupportedException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency path evidence is unsupported: {exception.Message}", headroom: headroom);
        }
        catch (Exception exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency evidence failed: {exception.Message}", headroom: headroom);
        }
    }

    internal static bool IsDeliberatelyPartialRepositoryRoot(string repositoryRoot) =>
        !Directory.Exists(Path.Combine(repositoryRoot, ".git")) &&
        !File.Exists(Path.Combine(repositoryRoot, ".git")) &&
        // Presence signal for declaration-reader selection only; Read fails closed when the dependent project is absent.
        !File.Exists(Path.Combine(repositoryRoot, "Mcg.AgentOrchestrator.sln")) &&
        !File.Exists(Path.Combine(
            repositoryRoot,
            InfrastructureTestProject.Replace('/', Path.DirectorySeparatorChar)));

    internal static RepositoryTestImpactHeadroom? MeasureIndexedSources(
        string repositoryRoot, int maximumIndexedSourceFiles)
    {
        try
        {
            repositoryRoot = Path.GetFullPath(repositoryRoot);
            var testProjectPath = ResolveWithinRepository(repositoryRoot, InfrastructureTestProject);
            if (testProjectPath is null || !File.Exists(testProjectPath)) return null;
            var closure = ReadIndexedSourceClosure(repositoryRoot, testProjectPath, out _);
            return closure is null ? null : new RepositoryTestImpactHeadroom(
                closure.Value.SourceOwnership.Count, maximumIndexedSourceFiles,
                null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Xml.XmlException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static (ProjectSnapshot[] Projects, Dictionary<string, bool> SourceOwnership)? ReadIndexedSourceClosure(
        string repositoryRoot, string testProjectPath, out string? failure,
        IReadOnlySet<string>? targetTestProjects = null)
    {
        targetTestProjects ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase) { testProjectPath };
        var projects = ReadProjectClosure(repositoryRoot, targetTestProjects, out failure);
        if (projects is null) return null;
        var sourceOwnership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var projectDirectory = Path.GetDirectoryName(project.Path)!;
            foreach (var sourcePath in FileSystemTestClassDeclarationReader
                .EnumerateProjectSourceFiles(projectDirectory))
            {
                sourceOwnership.TryAdd(
                    sourcePath,
                    targetTestProjects.Contains(project.Path));
            }
        }
        return (projects, sourceOwnership);
    }

    private static ProjectSnapshot[]? ReadProjectClosure(
        string repositoryRoot,
        IEnumerable<string> targetTestProjects,
        out string? failure)
    {
        failure = null;
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<ProjectSnapshot>();
        var pending = new Queue<string>();
        foreach (var target in targetTestProjects.Order(StringComparer.Ordinal)) pending.Enqueue(target);
        while (pending.Count > 0)
        {
            var projectPath = pending.Dequeue();
            if (!found.Add(projectPath))
            {
                continue;
            }

            var projectSource = SelectionFileReadProbe.ReadAllText(projectPath);
            var document = XDocument.Parse(projectSource, LoadOptions.None);
            projects.Add(new ProjectSnapshot(projectPath, HashText(projectSource)));
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var projectReferences = document
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("ProjectReference", StringComparison.Ordinal))
                .ToArray();
            if (projectReferences.Any(reference =>
                string.IsNullOrWhiteSpace(reference.Attribute("Include")?.Value)))
            {
                failure = $"ProjectReference in '{ToRepositoryPath(repositoryRoot, projectPath)}' has no Include path.";
                return null;
            }

            foreach (var reference in projectReferences
                .Select(element => element.Attribute("Include")!.Value)
                .Order(StringComparer.Ordinal))
            {
                var referencedPath = Path.GetFullPath(Path.Combine(projectDirectory, reference));
                if (!IsWithinRepository(repositoryRoot, referencedPath) || !File.Exists(referencedPath))
                {
                    failure = $"Project reference is outside the repository or unreadable: {reference}";
                    return null;
                }

                pending.Enqueue(referencedPath);
            }
        }

        return projects.OrderBy(project => project.Path, StringComparer.Ordinal).ToArray();
    }

    private static SourceSnapshot? ReadSnapshot(
        string repositoryRoot,
        IReadOnlyList<ProjectSnapshot> projects,
        IReadOnlyDictionary<string, bool> sourceOwnership,
        out string? failure, SelectionSourceTexts? sourceTexts, IReadOnlySet<string> targetTestProjects)
    {
        failure = null;
        var sources = new List<SourceFileSnapshot>(sourceOwnership.Count);
        long sourceBytes = 0;
        foreach (var pair in sourceOwnership.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var before = ReadFileVersion(pair.Key);
            var source = sourceTexts is null ? SelectionFileReadProbe.ReadAllText(pair.Key) : sourceTexts.Read(pair.Key);
            var after = ReadFileVersion(pair.Key);
            if (before != after)
            {
                failure =
                    $"Reverse-dependency source changed while it was indexed: {ToRepositoryPath(repositoryRoot, pair.Key)}";
                return null;
            }

            sourceBytes += after.Length;
            sources.Add(new SourceFileSnapshot(
                pair.Key,
                pair.Value,
                source,
                HashText(source),
                after));
        }

        foreach (var project in projects)
        {
            if (!HashText(SelectionFileReadProbe.ReadAllText(project.Path)).Equals(project.ContentHash, StringComparison.Ordinal))
            {
                failure =
                    $"Reverse-dependency project graph changed while it was indexed: {ToRepositoryPath(repositoryRoot, project.Path)}";
                return null;
            }
        }

        var currentOwnership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            foreach (var sourcePath in FileSystemTestClassDeclarationReader.EnumerateProjectSourceFiles(
                Path.GetDirectoryName(project.Path)!))
            {
                currentOwnership.TryAdd(
                    sourcePath,
                    targetTestProjects.Contains(project.Path));
            }
        }

        if (currentOwnership.Count != sourceOwnership.Count ||
            currentOwnership.Any(pair =>
                !sourceOwnership.TryGetValue(pair.Key, out var ownership) || ownership != pair.Value))
        {
            failure = "Reverse-dependency source membership changed while it was indexed.";
            return null;
        }

        foreach (var source in sources)
        {
            if (ReadFileVersion(source.Path) != source.Version)
            {
                failure =
                    $"Reverse-dependency source changed while it was indexed: {ToRepositoryPath(repositoryRoot, source.Path)}";
                return null;
            }
        }

        var repositoryIdentity = ReadRepositoryIdentity(repositoryRoot);
        var fingerprint = BuildFingerprint(
            repositoryRoot,
            repositoryIdentity,
            projects,
            sources);
        return new SourceSnapshot(
            fingerprint,
            repositoryIdentity,
            projects,
            sources,
            sourceBytes);
    }

    private static ReverseDependencyIndex? ParseSnapshot(
        SourceSnapshot snapshot,
        string repositoryRoot,
        out string? failure,
        out int reparsedFileCount)
    {
        failure = null;
        reparsedFileCount = 0;
        var files = new Dictionary<string, IndexedSourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in snapshot.Sources)
        {
            reparsedFileCount++;
            if (!CSharpTestClassScanner.TryReadSourceSymbols(source.Source, out var symbols))
            {
                failure =
                    $"Reverse-dependency source could not be parsed: {ToRepositoryPath(repositoryRoot, source.Path)}";
                return null;
            }

            files.Add(
                source.Path,
                new IndexedSourceFile(source.Path, source.IsTargetTestProject, symbols));
        }

        var knownMembersByType = files.Values
            .SelectMany(file => file.Symbols!.DeclaredTypes)
            .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.SelectMany(declaration => declaration.Members)
                    .Select(member => member.Name).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        return new ReverseDependencyIndex(files, knownMembersByType);
    }

    private static bool TryGetCachedIndex(
        string fingerprint,
        out ReverseDependencyIndex index,
        out int retainedSnapshotCount)
    {
        lock (CacheLock)
        {
            if (!CacheByFingerprint.TryGetValue(fingerprint, out var node))
            {
                index = null!;
                retainedSnapshotCount = CacheByFingerprint.Count;
                return false;
            }

            CacheRecency.Remove(node);
            CacheRecency.AddFirst(node);
            index = node.Value.Index;
            retainedSnapshotCount = CacheByFingerprint.Count;
            return true;
        }
    }

    private static int RetainIndex(string fingerprint, ReverseDependencyIndex index)
    {
        lock (CacheLock)
        {
            if (CacheByFingerprint.TryGetValue(fingerprint, out var existing))
            {
                CacheRecency.Remove(existing);
                CacheRecency.AddFirst(existing);
                return CacheByFingerprint.Count;
            }

            var node = CacheRecency.AddFirst(new CachedIndex(fingerprint, index));
            CacheByFingerprint.Add(fingerprint, node);
            while (CacheByFingerprint.Count > MaximumRetainedSnapshots)
            {
                var expired = CacheRecency.Last!;
                CacheRecency.RemoveLast();
                CacheByFingerprint.Remove(expired.Value.Fingerprint);
            }

            return CacheByFingerprint.Count;
        }
    }

    private static int RetainedSnapshotCount()
    {
        lock (CacheLock)
        {
            return CacheByFingerprint.Count;
        }
    }

    internal static void ClearCacheForTests()
    {
        lock (CacheLock)
        {
            CacheByFingerprint.Clear();
            CacheRecency.Clear();
        }
    }

    private static ReverseDependencyCacheReceipt CreateReceipt(
        SourceSnapshot snapshot,
        ReverseDependencyCacheDisposition disposition,
        int reparsedFileCount,
        int retainedSnapshotCount) =>
        new(
            disposition,
            snapshot.Fingerprint,
            snapshot.Sources.Count,
            snapshot.SourceBytes,
            reparsedFileCount,
            retainedSnapshotCount);

    private static string BuildFingerprint(
        string repositoryRoot,
        string repositoryIdentity,
        IReadOnlyList<ProjectSnapshot> projects,
        IReadOnlyList<SourceFileSnapshot> sources)
    {
        var builder = new StringBuilder();
        AppendIdentity(builder, CacheSchema);
        AppendIdentity(builder, Path.GetFullPath(repositoryRoot));
        AppendIdentity(builder, repositoryIdentity);
        foreach (var project in projects)
        {
            AppendIdentity(builder, ToRepositoryPath(repositoryRoot, project.Path));
            AppendIdentity(builder, project.ContentHash);
        }

        foreach (var source in sources)
        {
            AppendIdentity(builder, ToRepositoryPath(repositoryRoot, source.Path));
            AppendIdentity(builder, source.IsTargetTestProject ? "test" : "production");
            AppendIdentity(builder, source.ContentHash);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendIdentity(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');

    private static string HashText(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static FileVersion ReadFileVersion(string path)
    {
        var info = new FileInfo(path);
        return new FileVersion(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static string ReadRepositoryIdentity(string repositoryRoot)
    {
        var markerPath = Path.Combine(repositoryRoot, ".git");
        if (!Directory.Exists(markerPath) && !File.Exists(markerPath))
        {
            return "git:none";
        }

        var gitDirectory = markerPath;
        var markerIdentity = "git:directory";
        if (File.Exists(markerPath))
        {
            var marker = SelectionFileReadProbe.ReadAllText(markerPath).Trim();
            markerIdentity = $"git:file:{HashText(marker)}";
            const string prefix = "gitdir:";
            if (!marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return markerIdentity;
            }

            var declaredDirectory = marker[prefix.Length..].Trim();
            gitDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, declaredDirectory));
        }

        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
        {
            return markerIdentity;
        }

        var head = SelectionFileReadProbe.ReadAllText(headPath).Trim();
        var resolved = head;
        const string refPrefix = "ref:";
        if (head.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var reference = head[refPrefix.Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
            resolved = TryReadGitReference(gitDirectory, reference) ?? head;
        }

        return $"{markerIdentity};head={head};resolved={resolved}";
    }

    private static string? TryReadGitReference(string gitDirectory, string reference)
    {
        var directPath = Path.Combine(gitDirectory, reference);
        if (File.Exists(directPath))
        {
            return SelectionFileReadProbe.ReadAllText(directPath).Trim();
        }

        var commonDirectory = gitDirectory;
        var commonDirectoryPath = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(commonDirectoryPath))
        {
            commonDirectory = Path.GetFullPath(Path.Combine(
                gitDirectory,
                SelectionFileReadProbe.ReadAllText(commonDirectoryPath).Trim()));
            var commonRefPath = Path.Combine(commonDirectory, reference);
            if (File.Exists(commonRefPath))
            {
                return SelectionFileReadProbe.ReadAllText(commonRefPath).Trim();
            }
        }

        var packedRefsPath = Path.Combine(commonDirectory, "packed-refs");
        if (!File.Exists(packedRefsPath))
        {
            return null;
        }

        var normalizedReference = reference.Replace(Path.DirectorySeparatorChar, '/');
        foreach (var line in SelectionFileReadProbe.ReadLines(packedRefsPath))
        {
            if (line.Length == 0 || line[0] is '#' or '^')
            {
                continue;
            }

            var separator = line.IndexOf(' ');
            if (separator > 0 && line[(separator + 1)..].Equals(normalizedReference, StringComparison.Ordinal))
            {
                return line[..separator];
            }
        }

        return null;
    }

    private static string NormalizeRepositoryPath(string path) =>
        path.Trim().Replace('\\', '/').TrimStart('/');

    private static string? ResolveWithinRepository(string repositoryRoot, string repositoryRelativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return IsWithinRepository(repositoryRoot, fullPath) ? fullPath : null;
    }

    private static bool IsWithinRepository(string repositoryRoot, string path)
    {
        var root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRepositoryPath(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

    private sealed record IndexedSourceFile(
        string Path,
        bool IsTargetTestProject,
        CSharpTestClassScanner.SourceSymbols? Symbols);

    private sealed record ReverseDependencyIndex(
        IReadOnlyDictionary<string, IndexedSourceFile> Files,
        IReadOnlyDictionary<string, HashSet<string>> KnownMembersByType);

    private sealed record CachedIndex(string Fingerprint, ReverseDependencyIndex Index);

    private sealed record ProjectSnapshot(string Path, string ContentHash);

    private sealed record SourceSnapshot(
        string Fingerprint,
        string RepositoryIdentity,
        IReadOnlyList<ProjectSnapshot> Projects,
        IReadOnlyList<SourceFileSnapshot> Sources,
        long SourceBytes);

    private sealed record SourceFileSnapshot(
        string Path,
        bool IsTargetTestProject,
        string Source,
        string ContentHash,
        FileVersion Version);

    private readonly record struct FileVersion(long Length, long LastWriteTimeUtcTicks);

    private static bool OverlapsAnyRange(
        int startLine,
        int endLine,
        IReadOnlyList<(int StartLine, int EndLine)> ranges) =>
        ranges.Any(range => startLine <= range.EndLine && range.StartLine <= endLine);

    private static IReadOnlySet<string>? SeedFilter(
        CSharpTestClassScanner.SourceTypeDeclaration declaration,
        IReadOnlyList<(int StartLine, int EndLine)>? ranges,
        bool isShippingFile)
    {
        if (ranges is not { Count: > 0 } || (!declaration.IsPartial && !isShippingFile))
            return null;

        var names = MemberNames(declaration.Members.Where(member => OverlapsAnyRange(member.StartLine, member.EndLine, ranges)));
        if (names is null || declaration.IsPartial)
            return names;

        if (declaration.UnattributedLines is not { } unattributedLines ||
            unattributedLines.Any(line => OverlapsAnyRange(line, line, ranges)) ||
            declaration.UnattributedIdentifiers is not { } unattributed ||
            declaration.Members.Any(member => member.ReferencedIdentifiers is null))
            return null;

        foreach (var (startLine, endLine) in ranges)
        {
            if (startLine <= declaration.EndLine && declaration.StartLine <= endLine &&
                !IsCoveredByMembers(startLine, endLine, declaration.Members))
                return null;
        }

        return CompleteMemberFilter(declaration.Members, names, unattributed);
    }

    private static bool IsCoveredByMembers(int startLine, int endLine, IReadOnlyList<SourceMemberDeclaration> members)
    {
        // The scanner emits member spans in source order.
        foreach (var member in members)
        {
            if (member.EndLine < startLine)
                continue;
            if (member.StartLine > startLine)
                return false;
            if (member.EndLine >= endLine)
                return true;

            startLine = member.EndLine + 1;
        }

        return false;
    }

    internal static IReadOnlySet<string>? ConsumerFilter(
        CSharpTestClassScanner.SourceTypeDeclaration declaration,
        IReadOnlyList<FrontierSymbol> frontier,
        IReadOnlyDictionary<string, HashSet<string>> knownMembersByType,
        IReadOnlySet<string>? shippingFrontier,
        bool hasMultipleReachedParts)
    {
        var fallback = declaration.IsPartial ? MemberNames(declaration.Members) : null;
        // Caller expansion is local to a declaration. Keep every member of each reached part
        // when calls can cross between those parts.
        if (shippingFrontier is null || declaration.UnattributedIdentifiers is not { } unattributed ||
            hasMultipleReachedParts ||
            declaration.Members.Any(member => member.ReferencedIdentifiers is null))
            return fallback;

        bool HasShippingPeer(IReadOnlySet<string> identifiers) => identifiers.Any(identifier =>
            identifier != declaration.Name && shippingFrontier.Contains(identifier));

        if (HasShippingPeer(unattributed))
            return fallback;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in frontier.Where(symbol => References(declaration, symbol, knownMembersByType)))
        {
            if (unattributed.Contains(symbol.Name) ||
                (symbol.MemberFilter is { } filter && unattributed.Overlaps(filter)))
                return fallback;

            var matchedMember = false;
            foreach (var member in declaration.Members)
            {
                var identifiers = member.ReferencedIdentifiers!;
                if (!identifiers.Contains(symbol.Name))
                    continue;

                if (symbol.MemberFilter is not null && !identifiers.Overlaps(symbol.MemberFilter))
                {
                    // A name-only use may reach the changed member indirectly (for example, through construction).
                    if (!knownMembersByType.TryGetValue(symbol.Name, out var knownMembers) || !identifiers.Overlaps(knownMembers))
                        return fallback;
                    continue;
                }

                matchedMember = true;
                names.Add(member.Name);
            }

            if (!matchedMember)
                return fallback;
        }

        // A forwarding member can reach the change through another consumer on this hop,
        // including through callbacks whose targets are outside this declaration.
        foreach (var member in declaration.Members)
            if (HasShippingPeer(member.ReferencedIdentifiers!))
                names.Add(member.Name);

        return CompleteMemberFilter(declaration.Members, names, unattributed) ?? fallback;
    }

    private static IReadOnlySet<string>? CompleteMemberFilter(
        IReadOnlyList<SourceMemberDeclaration> members,
        HashSet<string> names,
        IReadOnlySet<string> unattributed)
    {
        if (names.Count == 0)
            return null;

        // Keep callers of matching members in this declaration, including chains of calls.
        int previousCount;
        do
        {
            previousCount = names.Count;
            foreach (var member in members)
                if (member.ReferencedIdentifiers!.Overlaps(names))
                    names.Add(member.Name);
        }
        while (names.Count != previousCount);

        return unattributed.Overlaps(names) ? null : names;
    }

    private static HashSet<string>? MemberNames(IEnumerable<SourceMemberDeclaration> members)
    {
        var names = members.Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        return names.Count > 0 ? names : null;
    }

    private static bool References(
        CSharpTestClassScanner.SourceTypeDeclaration declaration,
        FrontierSymbol symbol,
        IReadOnlyDictionary<string, HashSet<string>> knownMembersByType) =>
        declaration.ReferencedIdentifiers.Contains(symbol.Name) &&
        (symbol.MemberFilter is null || declaration.ReferencedIdentifiers.Overlaps(symbol.MemberFilter) ||
         !knownMembersByType.TryGetValue(symbol.Name, out var knownMembers) ||
         !declaration.ReferencedIdentifiers.Overlaps(knownMembers));

    private static FrontierSymbol[] MergeFrontier(IEnumerable<FrontierSymbol> symbols) =>
        symbols.GroupBy(symbol => symbol.Name, StringComparer.Ordinal)
            .Select(group => new FrontierSymbol(
                group.Key,
                group.Any(symbol => symbol.TestSelectionMode == TestSelectionMode.AllReferencingClasses)
                    ? TestSelectionMode.AllReferencingClasses
                    : TestSelectionMode.OwnedClassOnly,
                group.Any(symbol => symbol.MemberFilter is null)
                    ? null
                    : group.SelectMany(symbol => symbol.MemberFilter!).ToHashSet(StringComparer.Ordinal)))
            .OrderBy(symbol => symbol.Name, StringComparer.Ordinal)
            .ToArray();

    internal sealed record FrontierSymbol(
        string Name,
        TestSelectionMode TestSelectionMode,
        IReadOnlySet<string>? MemberFilter = null);

    internal enum TestSelectionMode
    {
        AllReferencingClasses,
        OwnedClassOnly
    }
}
