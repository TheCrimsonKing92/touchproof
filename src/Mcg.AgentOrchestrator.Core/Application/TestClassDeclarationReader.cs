using System.Collections.Concurrent;

namespace Mcg.AgentOrchestrator.Core;

internal enum TestClassDeclarationOutcome
{
    Resolved,
    NoQualifyingClass,
    Unreadable,
    Unavailable
}

internal sealed record TestClassDeclarations(
    TestClassDeclarationOutcome Outcome,
    IReadOnlyList<string> ClassNames)
{
    internal static TestClassDeclarations Resolved(IEnumerable<string> classNames) =>
        new(
            TestClassDeclarationOutcome.Resolved,
            classNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    internal static TestClassDeclarations NoQualifyingClass { get; } =
        new(TestClassDeclarationOutcome.NoQualifyingClass, []);

    internal static TestClassDeclarations Unreadable { get; } =
        new(TestClassDeclarationOutcome.Unreadable, []);

    internal static TestClassDeclarations Unavailable { get; } =
        new(TestClassDeclarationOutcome.Unavailable, []);
}

internal enum ReverseDependencySelectionOutcome
{
    Resolved,
    Unreadable,
    Unavailable,
    Abandoned
}

internal enum ReverseDependencyCacheDisposition
{
    Hit,
    Miss,
    Bypass
}

internal sealed record ReverseDependencyCacheReceipt(
    ReverseDependencyCacheDisposition Disposition,
    string Fingerprint,
    int IndexedFileCount,
    long IndexedSourceBytes,
    int ReparsedFileCount,
    int RetainedSnapshotCount)
{
    internal string Render() =>
        $"reverse-dependency-cache={Disposition.ToString().ToLowerInvariant()} " +
        $"fingerprint={Fingerprint} indexed-files={IndexedFileCount} " +
        $"source-bytes={IndexedSourceBytes} reparsed-files={ReparsedFileCount} " +
        $"retained-snapshots={RetainedSnapshotCount}";
}

internal sealed record ReverseDependencyTestSelection(
    ReverseDependencySelectionOutcome Outcome,
    IReadOnlyList<string> TestClassNames,
    string? Reason,
    ReverseDependencyCacheReceipt? CacheReceipt = null,
    ReverseDependencyDegradationKind? DegradationKind = null,
    RepositoryTestImpactHeadroom? Headroom = null)
{
    internal static ReverseDependencyTestSelection Resolved(
        IEnumerable<string> testClassNames,
        ReverseDependencyCacheReceipt? cacheReceipt = null,
        RepositoryTestImpactHeadroom? headroom = null) =>
        new(
            ReverseDependencySelectionOutcome.Resolved,
            testClassNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            null,
            cacheReceipt, Headroom: headroom);

    internal static ReverseDependencyTestSelection Unreadable(
        string reason,
        ReverseDependencyCacheReceipt? cacheReceipt = null,
        RepositoryTestImpactHeadroom? headroom = null) =>
        new(ReverseDependencySelectionOutcome.Unreadable, [], reason, cacheReceipt,
            ReverseDependencyDegradationKind.Unreadable, headroom);

    internal static ReverseDependencyTestSelection Abandoned(
        ReverseDependencyDegradationKind kind,
        string reason,
        ReverseDependencyCacheReceipt? cacheReceipt = null,
        RepositoryTestImpactHeadroom? headroom = null) =>
        new(ReverseDependencySelectionOutcome.Abandoned, [], reason, cacheReceipt, kind, headroom);

    internal static ReverseDependencyTestSelection Unavailable { get; } =
        new(ReverseDependencySelectionOutcome.Unavailable, [], "Reverse-dependency evidence is unavailable.");
}

internal interface ITestClassDeclarationReader
{
    TestClassDeclarations ReadFile(string repositoryRelativePath);

    TestClassDeclarations ReadProject(string repositoryRelativeDirectory);

    string? ReadNestedProjectFile(string projectDirectory, string sourcePath) => null;

    ReverseDependencyTestSelection ReadReverseDependentTestClasses(
        IReadOnlyList<string> changedSourcePaths) => ReverseDependencyTestSelection.Unavailable;
}

internal sealed class FileSystemTestClassDeclarationReader : ITestClassDeclarationReader
{
    private static readonly ConcurrentDictionary<ProjectCacheKey, TestClassDeclarations> ProjectCache = new();
    // Directory.Build.props declares the additional compile exclusions; the scope tests guard drift.
    private static readonly string[] ExcludedDirectorySegments = ["bin", "obj", "artifacts", ".scratch"];
    private readonly string _repositoryRoot;
    private readonly int _maximumIndexedSourceFiles;
    private readonly SelectionSourceTexts? _sourceTexts;

    internal FileSystemTestClassDeclarationReader(
        string repositoryRoot,
        int maximumIndexedSourceFiles = ReverseDependencyTestImpactReader.MaximumIndexedSourceFiles,
        SelectionSourceTexts? sourceTexts = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _maximumIndexedSourceFiles = maximumIndexedSourceFiles;
        _sourceTexts = sourceTexts;
    }

    internal static ITestClassDeclarationReader CreateForCurrentRepository()
    {
        foreach (var candidate in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return new FileSystemTestClassDeclarationReader(directory.FullName);
                }

                directory = directory.Parent;
            }
        }

        return UnavailableTestClassDeclarationReader.Instance;
    }

    public TestClassDeclarations ReadFile(string repositoryRelativePath)
    {
        try
        {
            var path = ResolveWithinRepository(repositoryRelativePath);
            return path is null || !File.Exists(path)
                ? TestClassDeclarations.Unreadable
                : ReadDeclarations(path);
        }
        catch
        {
            return TestClassDeclarations.Unreadable;
        }
    }

    public TestClassDeclarations ReadProject(string repositoryRelativeDirectory)
    {
        try
        {
            var directory = ResolveWithinRepository(repositoryRelativeDirectory);
            if (directory is null || !Directory.Exists(directory))
            {
                return TestClassDeclarations.Unreadable;
            }

            var sourceFiles = EnumerateProjectSourceFiles(directory);
            // Selection texts belong to an earlier read, not the current filesystem stamp.
            if (_sourceTexts is not null) return ReadProjectDeclarations(sourceFiles);
            var newestWrite = sourceFiles.Length == 0
                ? 0
                : sourceFiles.Max(path => File.GetLastWriteTimeUtc(path).Ticks);
            var cacheKey = new ProjectCacheKey(directory, sourceFiles.Length, newestWrite);
            return ProjectCache.GetOrAdd(cacheKey, _ => ReadProjectDeclarations(sourceFiles));
        }
        catch
        {
            return TestClassDeclarations.Unreadable;
        }
    }

    public string? ReadNestedProjectFile(string projectDirectory, string sourcePath) =>
        RepositoryProjectOwnership.FindNestedProjectFile(_repositoryRoot, projectDirectory, sourcePath);

    public ReverseDependencyTestSelection ReadReverseDependentTestClasses(
        IReadOnlyList<string> changedSourcePaths) =>
        ReverseDependencyTestImpactReader.Read(
            _repositoryRoot, changedSourcePaths, maximumIndexedSourceFiles: _maximumIndexedSourceFiles,
            sourceTexts: _sourceTexts);

    private TestClassDeclarations ReadProjectDeclarations(string[] sourceFiles)
    {
        var classNames = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            var declarations = ReadDeclarations(sourceFile);
            if (declarations.Outcome == TestClassDeclarationOutcome.Unreadable)
            {
                return TestClassDeclarations.Unreadable;
            }

            classNames.AddRange(declarations.ClassNames);
        }

        return classNames.Count == 0
            ? TestClassDeclarations.NoQualifyingClass
            : TestClassDeclarations.Resolved(classNames);
    }

    private TestClassDeclarations ReadDeclarations(string path)
    {
        var source = _sourceTexts is null ? SelectionFileReadProbe.ReadAllText(path) : _sourceTexts.Read(path);
        return CSharpTestClassScanner.TryReadClassNames(source, out var classNames)
            ? classNames.Count == 0
                ? TestClassDeclarations.NoQualifyingClass
                : TestClassDeclarations.Resolved(classNames)
            : TestClassDeclarations.Unreadable;
    }

    internal static string[] EnumerateProjectSourceFiles(string projectDirectory)
    {
        projectDirectory = Path.GetFullPath(projectDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nestedProjectDirectories = Directory
            .EnumerateFiles(projectDirectory, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                !path.Equals(projectDirectory, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasGeneratedPathSegment(Path.GetRelativePath(projectDirectory, path)))
            .Where(path => !nestedProjectDirectories.Any(directory => IsWithinDirectory(path, directory)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string? ResolveWithinRepository(string repositoryRelativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            _repositoryRoot,
            repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return IsWithinDirectory(fullPath, _repositoryRoot) ? fullPath : null;
    }

    private static bool HasGeneratedPathSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment =>
            ExcludedDirectorySegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static bool IsWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProjectCacheKey(string Directory, int FileCount, long NewestWriteTicks);
}

internal sealed class UnavailableTestClassDeclarationReader : ITestClassDeclarationReader
{
    internal static UnavailableTestClassDeclarationReader Instance { get; } = new();

    private UnavailableTestClassDeclarationReader()
    {
    }

    public TestClassDeclarations ReadFile(string repositoryRelativePath) => TestClassDeclarations.Unavailable;

    public TestClassDeclarations ReadProject(string repositoryRelativeDirectory) => TestClassDeclarations.Unavailable;
}

internal static class CSharpTestClassScanner
{
    internal sealed record SourceTypeDeclaration(
        string Name,
        bool IsPartial,
        IReadOnlySet<string> ReferencedIdentifiers,
        int StartLine,
        int EndLine,
        IReadOnlyList<SourceMemberDeclaration> Members,
        IReadOnlyList<SourceTypeDeclaration> NestedTypes);

    internal sealed record SourceSymbols(
        IReadOnlyList<SourceTypeDeclaration> DeclaredTypes,
        IReadOnlyList<string> TestClassNames);

    internal static bool TryReadClassNames(string source, out IReadOnlyList<string> classNames)
    {
        classNames = [];
        if (!TryReadSourceSymbols(source, out var symbols))
        {
            return false;
        }

        classNames = symbols.TestClassNames;
        return true;
    }

    internal static bool TryReadSourceSymbols(string source, out SourceSymbols symbols)
    {
        symbols = new SourceSymbols([], []);
        if (!TryTokenize(source, out var tokens) || !TryFindDeclarations(tokens, out var declarations))
        {
            return false;
        }

        var topLevelDeclarations = declarations
            .Where(declaration => !declarations.Any(parent =>
                parent.OpenBraceIndex < declaration.KeywordIndex &&
                declaration.KeywordIndex < parent.CloseBraceIndex))
            .ToArray();
        var classNames = topLevelDeclarations
            .Where(declaration => declaration.IsQualifyingClass)
            .Where(declaration => ContainsTestMethodAttribute(tokens, declaration, declarations))
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declarationNameIndexes = declarations
            .Select(declaration => declaration.NameIndex)
            .ToHashSet();
        var declarationsByKeyword = declarations.ToDictionary(declaration => declaration.KeywordIndex);
        var sourceTypeDeclarations = topLevelDeclarations
            .Select(ReadSourceTypeDeclaration)
            .OrderBy(declaration => declaration.Name, StringComparer.Ordinal)
            .ToArray();
        symbols = new SourceSymbols(
            sourceTypeDeclarations,
            classNames);
        return true;

        SourceTypeDeclaration ReadSourceTypeDeclaration(TypeDeclaration declaration)
        {
            var members = new List<SourceMemberDeclaration>();
            var nestedTypes = new List<SourceTypeDeclaration>();
            if (tokens[declaration.KeywordIndex].Value == "record")
            {
                var parameterStart = declaration.NameIndex + 1;
                if (tokens[parameterStart].Value == "<")
                {
                    parameterStart = FindMatching(tokens, parameterStart, "<", ">") + 1;
                }

                if (parameterStart > declaration.NameIndex && tokens[parameterStart].Value == "(")
                {
                    var parameterEnd = FindMatching(tokens, parameterStart, "(", ")");
                    if (parameterEnd > parameterStart && parameterEnd <= declaration.CloseBraceIndex)
                    {
                        ReadNamedSegments(tokens, parameterStart + 1, parameterEnd, members);
                    }
                }
            }

            for (var start = declaration.OpenBraceIndex + 1; start < declaration.CloseBraceIndex;)
            {
                if (tokens[start].Value == "#")
                {
                    var directiveLine = tokens[start].Line;
                    do { start++; } while (start < declaration.CloseBraceIndex &&
                                          tokens[start].Line == directiveLine);
                    continue;
                }

                var end = ReadMember(tokens, declaration, declarationsByKeyword, start, members, out var nested);
                if (nested is not null)
                {
                    nestedTypes.Add(ReadSourceTypeDeclaration(nested));
                }

                start = end + 1;
            }

            return new SourceTypeDeclaration(
                declaration.Name,
                declaration.IsPartial,
                ReadReferencedIdentifiers(tokens, declaration, declarationNameIndexes),
                tokens[FindDeclarationStart(tokens, declaration.KeywordIndex)].Line,
                tokens[declaration.CloseBraceIndex].Line,
                members.Count == 0 ? [] : members.ToArray(),
                nestedTypes.Count == 0 ? [] : nestedTypes.ToArray());
        }
    }

    private static IReadOnlySet<string> ReadReferencedIdentifiers(
        IReadOnlyList<Token> tokens,
        TypeDeclaration declaration,
        IReadOnlySet<int> declarationNameIndexes) =>
        Enumerable.Range(declaration.KeywordIndex, declaration.CloseBraceIndex - declaration.KeywordIndex + 1)
            .Where(index => tokens[index].IsIdentifier && !declarationNameIndexes.Contains(index))
            .Select(index => tokens[index].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static int ReadMember(
        IReadOnlyList<Token> tokens,
        TypeDeclaration owner,
        IReadOnlyDictionary<int, TypeDeclaration> declarationsByKeyword,
        int start,
        List<SourceMemberDeclaration> members,
        out TypeDeclaration? nested)
    {
        nested = null;
        var end = owner.CloseBraceIndex;
        var methodName = -1;
        var genericName = -1;
        var propertyName = -1;
        var constantStart = -1;
        var hasExpression = false;
        var excluded = false;

        for (var index = start; index < owner.CloseBraceIndex; index++)
        {
            var value = tokens[index].Value;
            if (!hasExpression && declarationsByKeyword.TryGetValue(index, out var candidate) &&
                candidate.CloseBraceIndex < owner.CloseBraceIndex &&
                FindDeclarationStart(tokens, index) == start)
            {
                members.Add(new SourceMemberDeclaration(candidate.Name, tokens[start].Line,
                    tokens[candidate.CloseBraceIndex].Line));
                nested = candidate;
                return candidate.CloseBraceIndex;
            }

            if (!hasExpression && (value is "event" or "operator" or "~") &&
                (index == start || tokens[index - 1].Value != "@"))
            {
                excluded = true;
            }

            if (!hasExpression && value == "const" && (index == start || tokens[index - 1].Value != "@"))
            {
                constantStart = index + 1;
            }

            if (value == ";")
            {
                end = index;
                break;
            }

            if (value is "=" or "=>")
            {
                if (!hasExpression && value == "=>" && index > start && tokens[index - 1].IsIdentifier)
                {
                    propertyName = index - 1;
                }

                hasExpression = true;
                continue;
            }

            var groupEnd = FindGroupEnd(tokens, index, includeAngles: !hasExpression && !excluded);
            if (groupEnd < 0 || groupEnd >= owner.CloseBraceIndex)
            {
                continue;
            }

            if (!hasExpression && value == "<")
            {
                genericName = tokens[groupEnd + 1].Value == "(" ? index - 1 : -1;
            }
            else if (!hasExpression && value == "(" && methodName < 0 && index > start &&
                     tokens[groupEnd + 1].Value is "where" or ":" or ";" or "{" or "=>")
            {
                var nameIndex = genericName >= 0 ? genericName : index - 1;
                if (tokens[nameIndex].IsIdentifier)
                {
                    methodName = nameIndex;
                }

                genericName = -1;
            }
            else if (!hasExpression && value == "{")
            {
                if (index > start && tokens[index - 1].IsIdentifier)
                {
                    propertyName = index - 1;
                }

                end = groupEnd;
                if (tokens[groupEnd + 1].Value != "=")
                {
                    break;
                }
            }

            index = groupEnd;
        }

        if (end >= owner.CloseBraceIndex || excluded)
        {
            return end;
        }

        if (constantStart >= 0)
        {
            ReadNamedSegments(tokens, constantStart, end, members, (tokens[start].Line, tokens[end].Line));
        }
        else if (methodName < 0 || tokens[methodName].Value != owner.Name ||
                 (methodName > start && tokens[methodName - 1].Value == "."))
        {
            var nameIndex = methodName >= 0 ? methodName : propertyName;
            if (nameIndex >= 0)
            {
                members.Add(new SourceMemberDeclaration(tokens[nameIndex].Value, tokens[start].Line, tokens[end].Line));
            }
        }

        return end;
    }

    private static void ReadNamedSegments(
        IReadOnlyList<Token> tokens,
        int start,
        int end,
        List<SourceMemberDeclaration> members,
        (int StartLine, int EndLine)? sharedSpan = null)
    {
        var segmentStart = start;
        var nameIndex = -1;
        var hasDefault = false;
        for (var index = start; index <= end; index++)
        {
            if (index == end || tokens[index].Value == ",")
            {
                if (nameIndex >= 0)
                {
                    var (startLine, endLine) = sharedSpan ?? (tokens[segmentStart].Line, tokens[index - 1].Line);
                    members.Add(new SourceMemberDeclaration(tokens[nameIndex].Value, startLine, endLine));
                }

                segmentStart = index + 1;
                nameIndex = -1;
                hasDefault = false;
                continue;
            }

            if (tokens[index].Value == "=")
            {
                hasDefault = true;
            }

            var groupEnd = FindGroupEnd(tokens, index, includeAngles: true);
            if (groupEnd > index && groupEnd < end &&
                (!hasDefault || tokens[index].Value != "<" || tokens[groupEnd + 1].Value == "."))
            {
                index = groupEnd;
            }
            else if (!hasDefault && tokens[index].IsIdentifier)
            {
                nameIndex = index;
            }
        }
    }

    private static int FindGroupEnd(IReadOnlyList<Token> tokens, int index, bool includeAngles)
    {
        var close = tokens[index].Value switch
        {
            "(" => ")",
            "[" => "]",
            "{" => "}",
            "<" when includeAngles => ">",
            _ => null
        };
        return close is null ? -1 : FindMatching(tokens, index, tokens[index].Value, close);
    }

    private static bool TryFindDeclarations(
        IReadOnlyList<Token> tokens,
        out IReadOnlyList<TypeDeclaration> declarations)
    {
        var found = new List<TypeDeclaration>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var keyword = tokens[index].Value;
            if (keyword is not ("class" or "record" or "struct" or "interface" or "enum"))
            {
                continue;
            }

            if (keyword is "class" or "struct" && index > 0 && tokens[index - 1].Value == "record")
            {
                continue;
            }

            var nameIndex = index + 1;
            var isRecordStruct = keyword == "record" &&
                nameIndex < tokens.Count &&
                tokens[nameIndex].Value == "struct";
            var isRecordClass = keyword == "record" &&
                nameIndex < tokens.Count &&
                tokens[nameIndex].Value == "class";
            if (isRecordStruct || isRecordClass)
            {
                nameIndex++;
            }

            if (nameIndex >= tokens.Count || !tokens[nameIndex].IsIdentifier)
            {
                continue;
            }

            var openBraceIndex = FindBodyOpenBrace(tokens, nameIndex + 1);
            int closeBraceIndex;
            if (openBraceIndex < 0)
            {
                var parameterListIndex = nameIndex + 1;
                if (parameterListIndex < tokens.Count && tokens[parameterListIndex].Value == "<")
                {
                    var genericEndIndex = FindMatching(tokens, parameterListIndex, "<", ">");
                    if (genericEndIndex < 0)
                    {
                        continue;
                    }

                    parameterListIndex = genericEndIndex + 1;
                }

                if (parameterListIndex >= tokens.Count)
                {
                    continue;
                }

                var headerEndIndex = parameterListIndex;
                if (tokens[parameterListIndex].Value == "(")
                {
                    var parameterEndIndex = FindMatching(tokens, parameterListIndex, "(", ")");
                    if (parameterEndIndex < 0)
                    {
                        continue;
                    }

                    headerEndIndex = parameterEndIndex + 1;
                }
                else if (keyword != "record" ||
                    tokens[parameterListIndex].Value is not (";" or ":" or "where") ||
                    !found.Exists(parent => parent.OpenBraceIndex < index && index < parent.CloseBraceIndex) ||
                    !StartsAtDeclarationBoundary(tokens, index))
                {
                    continue;
                }

                var endIndex = nameIndex + 1;
                while (endIndex < tokens.Count && tokens[endIndex].Value is not ("{" or ";" or "=>"))
                {
                    endIndex++;
                }

                if (endIndex >= tokens.Count || tokens[endIndex].Value != ";" || headerEndIndex > endIndex)
                {
                    continue;
                }

                // An empty body span keeps nesting and test-method detection unchanged.
                openBraceIndex = closeBraceIndex = endIndex;
            }
            else
            {
                closeBraceIndex = FindMatching(tokens, openBraceIndex, "{", "}");
                if (closeBraceIndex < 0)
                {
                    declarations = [];
                    return false;
                }
            }

            var modifiers = ReadModifiers(tokens, index);
            found.Add(new TypeDeclaration(
                index,
                nameIndex,
                openBraceIndex,
                closeBraceIndex,
                tokens[nameIndex].Value,
                modifiers.Contains("partial"),
                IsQualifyingClass: keyword is "class" or "record" &&
                    !isRecordStruct &&
                    modifiers.Contains("public") &&
                    !modifiers.Contains("abstract") &&
                    !modifiers.Contains("static")));
        }

        declarations = found;
        return BracesAreBalanced(tokens);
    }

    private static bool StartsAtDeclarationBoundary(IReadOnlyList<Token> tokens, int keywordIndex)
    {
        var start = FindDeclarationStart(tokens, keywordIndex);
        var previous = start - 1;
        if (previous < 0 || tokens[previous].Value is "{" or "}" or ";")
        {
            return true;
        }

        var previousLine = tokens[previous].Line;
        while (previous > 0 && tokens[previous - 1].Line == previousLine)
        {
            previous--;
        }

        return tokens[previous].Value == "#" && previousLine < tokens[start].Line;
    }

    private static int FindBodyOpenBrace(IReadOnlyList<Token> tokens, int startIndex)
    {
        for (var index = startIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Value == "{")
            {
                return index;
            }

            if (tokens[index].Value is ";" or "=>")
            {
                return -1;
            }
        }

        return -1;
    }

    private static int FindDeclarationStart(IReadOnlyList<Token> tokens, int keywordIndex)
    {
        var start = keywordIndex;
        while (start > 0)
        {
            if (tokens[start - 1].Value is "public" or "internal" or "private" or "protected" or
                "abstract" or "sealed" or "static" or "partial" or "readonly" or "ref" or "unsafe" or "new" or "file")
            {
                start--;
                continue;
            }
            if (tokens[start - 1].Value != "]") break;
            var depth = 1;
            var index = start - 2;
            for (; index >= 0; index--)
            {
                if (tokens[index].Value == "]") depth++;
                if (tokens[index].Value == "[" && --depth == 0) break;
            }
            if (index < 0) break;
            start = index;
        }
        return start;
    }

    private static HashSet<string> ReadModifiers(IReadOnlyList<Token> tokens, int keywordIndex)
    {
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = keywordIndex - 1; index >= 0; index--)
        {
            if (tokens[index].Value is "{" or "}" or ";")
            {
                break;
            }

            modifiers.Add(tokens[index].Value);
        }

        return modifiers;
    }

    private static bool ContainsTestMethodAttribute(
        IReadOnlyList<Token> tokens,
        TypeDeclaration declaration,
        IReadOnlyList<TypeDeclaration> declarations)
    {
        for (var index = declaration.OpenBraceIndex + 1; index < declaration.CloseBraceIndex; index++)
        {
            var nested = declarations.FirstOrDefault(candidate =>
                candidate.KeywordIndex > declaration.OpenBraceIndex &&
                candidate.KeywordIndex < declaration.CloseBraceIndex &&
                candidate.OpenBraceIndex <= index &&
                index <= candidate.CloseBraceIndex);
            if (nested is not null)
            {
                index = nested.CloseBraceIndex;
                continue;
            }

            if (tokens[index].Value != "[")
            {
                continue;
            }

            var closeBracket = FindMatching(tokens, index, "[", "]");
            if (closeBracket < 0 || closeBracket >= declaration.CloseBraceIndex)
            {
                return false;
            }

            for (var attributeIndex = index + 1; attributeIndex < closeBracket; attributeIndex++)
            {
                if (!tokens[attributeIndex].IsIdentifier)
                {
                    continue;
                }

                var name = tokens[attributeIndex].Value;
                if (name.EndsWith("Fact", StringComparison.Ordinal) ||
                    name.EndsWith("Theory", StringComparison.Ordinal) ||
                    name.EndsWith("FactAttribute", StringComparison.Ordinal) ||
                    name.EndsWith("TheoryAttribute", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            index = closeBracket;
        }

        return false;
    }

    private static bool BracesAreBalanced(IReadOnlyList<Token> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Value == "{")
            {
                depth++;
            }
            else if (token.Value == "}" && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private static int FindMatching(
        IReadOnlyList<Token> tokens,
        int openIndex,
        string openToken,
        string closeToken)
    {
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Value == openToken)
            {
                depth++;
            }
            else if (tokens[index].Value == closeToken && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryTokenize(string source, out IReadOnlyList<Token> tokens)
    {
        var found = new List<Token>();
        var line = 1;
        var lineCursor = 0;
        Token At(string value, bool isIdentifier, int position)
        {
            // Count skipped comments and literals as well as whitespace, once per character.
            while (lineCursor < position)
                if (source[lineCursor++] == '\n') line++;
            return new Token(value, isIdentifier, line);
        }
        for (var index = 0; index < source.Length;)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not ('\r' or '\n'))
                {
                    index++;
                }
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens = [];
                    return false;
                }

                index = end + 2;
                continue;
            }

            if (TrySkipStringOrCharacter(source, ref index, out var recognized))
            {
                // A literal contributes no identifiers, but can end a positional parameter.
                found.Add(At("literal", false, index - 1));
                continue;
            }

            if (recognized)
            {
                tokens = [];
                return false;
            }

            if (char.IsLetter(source[index]) || source[index] == '_')
            {
                var start = index++;
                while (index < source.Length &&
                    (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                {
                    index++;
                }

                found.Add(At(source[start..index], true, start));
                continue;
            }

            if (source[index] == '=' && index + 1 < source.Length && source[index + 1] == '>')
            {
                found.Add(At("=>", false, index));
                index += 2;
                continue;
            }

            found.Add(At(source[index].ToString(), false, index));
            index++;
        }

        tokens = found;
        return true;
    }

    private static bool TrySkipStringOrCharacter(string source, ref int index, out bool recognized)
    {
        recognized = false;
        var start = index;
        if (source[index] == '\'')
        {
            recognized = true;
            index++;
            while (index < source.Length)
            {
                if (source[index] == '\\')
                {
                    index += 2;
                }
                else if (source[index++] == '\'')
                {
                    return true;
                }
            }

            return false;
        }

        var prefixIndex = index;
        var verbatim = false;
        var interpolated = false;
        while (prefixIndex < source.Length && source[prefixIndex] == '$')
        {
            interpolated = true;
            prefixIndex++;
        }
        if (prefixIndex < source.Length && source[prefixIndex] == '@')
        {
            verbatim = true;
            prefixIndex++;
            if (prefixIndex < source.Length && source[prefixIndex] == '$')
            {
                interpolated = true;
                prefixIndex++;
            }
        }
        else if (prefixIndex == index && source[prefixIndex] == '@')
        {
            verbatim = true;
            prefixIndex++;
            if (prefixIndex < source.Length && source[prefixIndex] == '$')
            {
                interpolated = true;
                prefixIndex++;
            }
        }

        if (prefixIndex >= source.Length || source[prefixIndex] != '"')
        {
            return false;
        }

        recognized = true;
        var quoteCount = 0;
        while (prefixIndex + quoteCount < source.Length && source[prefixIndex + quoteCount] == '"')
        {
            quoteCount++;
        }

        if (quoteCount >= 3 && !verbatim)
        {
            index = prefixIndex + quoteCount;
            while (index < source.Length)
            {
                var closingQuotes = 0;
                while (index + closingQuotes < source.Length && source[index + closingQuotes] == '"')
                {
                    closingQuotes++;
                }
                if (closingQuotes >= quoteCount)
                {
                    index += quoteCount;
                    return true;
                }

                index++;
            }

            return false;
        }

        index = prefixIndex + 1;
        var interpolationDepth = 0;
        while (index < source.Length)
        {
            if (interpolated && interpolationDepth > 0)
            {
                if (TrySkipStringOrCharacter(source, ref index, out var nestedRecognized))
                {
                    continue;
                }

                if (nestedRecognized)
                {
                    index = start;
                    return false;
                }
            }

            if (interpolated && source[index] == '{')
            {
                if (interpolationDepth == 0 &&
                    index + 1 < source.Length && source[index + 1] == '{')
                {
                    index += 2;
                }
                else
                {
                    interpolationDepth++;
                    index++;
                }
            }
            else if (interpolated && interpolationDepth > 0 && source[index] == '}')
            {
                interpolationDepth--;
                index++;
            }
            else if (interpolationDepth == 0 && verbatim && source[index] == '"' &&
                index + 1 < source.Length && source[index + 1] == '"')
            {
                index += 2;
            }
            else if (interpolationDepth == 0 && !verbatim && source[index] == '\\')
            {
                index += 2;
            }
            else if (interpolationDepth == 0 && source[index] == '"')
            {
                index++;
                return true;
            }
            else
            {
                index++;
            }
        }

        index = start;
        return false;
    }

    private sealed record Token(string Value, bool IsIdentifier, int Line);

    private sealed record TypeDeclaration(
        int KeywordIndex,
        int NameIndex,
        int OpenBraceIndex,
        int CloseBraceIndex,
        string Name,
        bool IsPartial,
        bool IsQualifyingClass);
}
