using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: read-only verified repository inspection; negative controls use in-memory sources.
public sealed class OwnerConsolePresentationPurityTests
{
    private const string ConsolePath = "src/Mcg.AgentOrchestrator.App/Console/";
    // Data-access and composition owners; presentation files remain subject to ForbiddenUses.
    private static readonly HashSet<string> Exemptions = new(StringComparer.Ordinal)
    {
        ConsolePath + "ChangeStreamFileReader.cs",
        ConsolePath + "ConductEventFileSource.cs",
        ConsolePath + "ExperimentDecideIntentSubmitter.cs",
        // Enqueues operator intents; presentation never writes executor configuration.
        ConsolePath + "ExecutorSettingIntentSubmitter.cs",
        ConsolePath + "OwnerActivityEvidenceReader.cs",
        ConsolePath + "OwnerAnswerIntentSubmitter.cs",
        ConsolePath + "OwnerConsoleAdapters.cs",
        // Owns catalog reads and atomic saves, not presentation.
        ConsolePath + "OwnerConsoleAgentsSettingsStore.cs",
        ConsolePath + "OwnerConsoleBoardComposer.cs",
        ConsolePath + "OwnerConsoleBoardSource.cs",
        ConsolePath + "OwnerConsoleEvidenceAttemptProgressReader.cs",
        ConsolePath + "OwnerConsoleFullScreenHost.cs",
        ConsolePath + "OwnerConsoleGateFailureNames.cs",
        ConsolePath + "OwnerConsoleGateLanePlanReader.cs",
        ConsolePath + "OwnerConsoleGateProgressLoader.cs",
        ConsolePath + "OwnerConsoleInputTraceOptions.cs",
        ConsolePath + "OwnerConsoleJsonlTail.cs",
        ConsolePath + "OwnerConsoleLandedToday.cs",
        // Reads lifecycle event files to reconcile the landed-today scan with stream deliveries.
        ConsolePath + "OwnerConsoleLandingBaseline.cs",
        ConsolePath + "OwnerConsoleNotificationSettings.cs",
        // owns conductor-policy reads and atomic saves, not presentation
        ConsolePath + "OwnerConsolePolicySettingsStore.cs",
        ConsolePath + "OwnerConsoleRefreshTimingsFile.cs",
        // The sound command owns library access and atomic settings persistence, not presentation.
        ConsolePath + "OwnerConsoleSoundCommand.cs",
        ConsolePath + "OwnerConsoleStartupActivity.cs",
        // Reads state and collaboration store file metadata, including WAL files, for change detection.
        ConsolePath + "OwnerConsoleStoreFingerprint.cs",
        ConsolePath + "OwnerConsoleVerb.cs",
        ConsolePath + "OwnerConsoleWorkspaceCatalog.cs",
        ConsolePath + "OwnerGateFailureEvidence.cs",
        ConsolePath + "OwnerGoalChangeSummaryQuery.cs",
        ConsolePath + "OwnerGoalLifecycleTail.cs",
        ConsolePath + "OwnerGoalSummarySectionReader.cs",
        // Reads bounded worker artifacts from retained output or their file paths.
        ConsolePath + "OwnerGoalVerifiedSectionReader.cs",
        ConsolePath + "OwnerJointGateFailureEvidence.cs",
        ConsolePath + "OwnerLandingsQuery.cs",
        ConsolePath + "OwnerQuestionReadModel.cs",
        ConsolePath + "OwnerQuestionReadModelFactory.cs",
        ConsolePath + "OwnerQuestionResolutionReader.cs",
        ConsolePath + "PortfolioGoalEpicLookup.cs",
        ConsolePath + "PortfolioGoalEpicTitleReader.cs",
        ConsolePath + "ProjectOnboardingCompletion.cs",
        ConsolePath + "ProjectOnboardingStateReader.cs",
        ConsolePath + "WorkspaceEpicProgressSource.cs"
    };

    [Fact]
    public void PresentationFiles_HaveNoFileSystemOrStorageUses()
    {
        var root = VerifiedRepositoryRoot.Find();
        var sources = Directory.EnumerateFiles(Path.Combine(root, ConsolePath), "*.cs", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
        Assert.NotEmpty(sources);
        Assert.Empty(Violations(sources, Exemptions));
    }

    [Theory]
    [InlineData("File.ReadAllText(\"x\")", "File")]
    [InlineData("Directory.GetFiles(\"x\")", "Directory")]
    [InlineData("new FileInfo(\"x\")", "FileInfo")]
    [InlineData("new FileStream(\"x\", FileMode.Open)", "FileStream")]
    [InlineData("new StreamReader(\"x\")", "StreamReader")]
    [InlineData("System.IO.File.ReadAllText(\"x\")", "File")]
    public void FileSystemUse_IsRejected(string expression, string type)
    {
        Assert.Equal([$"View.cs: file-system use {type}"],
            Violations(new Dictionary<string, string> { ["View.cs"] = $"class View {{ object Read() => {expression}; }}" }, []));
    }

    [Theory]
    [InlineData("SqliteConnection")]
    [InlineData("SqliteOperatorIntentStore")]
    [InlineData("PortfolioStore")]
    public void StorageUse_IsRejected(string type)
    {
        Assert.Equal([$"View.cs: storage use {type}"],
            Violations(new Dictionary<string, string> { ["View.cs"] = $"class View {{ {type} store; }}" }, []));
    }

    [Theory]
    [InlineData("using Reader = System.IO.StreamReader;", "StreamReader")]
    [InlineData("using static System.IO.File;", "File")]
    public void AliasAndStaticImports_AreRejected(string source, string type)
    {
        Assert.Equal([$"View.cs: file-system use {type}"],
            Violations(new Dictionary<string, string> { ["View.cs"] = source }, []));
    }

    [Theory]
    [InlineData("using IO = System.IO; class View { object Read() => IO.File.ReadAllText(\"x\"); }", "file-system use File")]
    [InlineData("class View { void Read() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }", "storage use SqliteConnection")]
    [InlineData("using Db = Microsoft.Data.Sqlite; class View { void Read() => Db.SqliteConnection.ClearAllPools(); }", "storage use SqliteConnection")]
    public void QualifiedAndNamespaceAliasedTypes_AreRejected(string source, string finding)
    {
        Assert.Equal(["View.cs: " + finding], Violations(new Dictionary<string, string> { ["View.cs"] = source }, []));
    }

    [Fact]
    public void IdenticalText_UnderNonExemptNameIsRejected()
    {
        const string source = "class Reader { string Read() => File.ReadAllText(\"x\"); }";
        var sources = new Dictionary<string, string> { ["Reader.cs"] = source, ["View.cs"] = source };
        Assert.Equal(["View.cs: file-system use File"], Violations(sources, ["Reader.cs"]));
    }

    [Fact]
    public void CommandPersistence_UnderPresentationNameIsRejected()
    {
        var commandPath = ConsolePath + "OwnerConsoleSoundCommand.cs";
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), commandPath));
        Assert.Contains(commandPath, Exemptions);
        var sources = new Dictionary<string, string> { [commandPath] = source, ["View.cs"] = source };
        Assert.Equal(["View.cs: file-system use Directory", "View.cs: file-system use File"],
            Violations(sources, [commandPath]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MissingExemption_IsReportedAsStale()
    {
        Assert.Equal(["Gone.cs: stale exemption"],
            Violations(new Dictionary<string, string> { ["View.cs"] = "class View {}" }, ["Gone.cs"]));
    }

    [Fact]
    public void CommentsStringsPropertiesAndArgumentNames_AreNotTypeUses()
    {
        const string source = """
            // File.ReadAllText("x"); PortfolioStore
            class View {
                string Text = "Directory.GetFiles";
                void Draw() { Show(Directory: entry.Directory.Length); }
            }
            """;
        Assert.Empty(Violations(new Dictionary<string, string> { ["View.cs"] = source }, []));
    }

    private static IReadOnlyList<string> Violations(IReadOnlyDictionary<string, string> sources, IEnumerable<string> exemptions)
    {
        var exempt = exemptions.ToHashSet(StringComparer.Ordinal);
        var findings = exempt.Where(path => !sources.ContainsKey(path)).Order(StringComparer.Ordinal)
            .Select(path => $"{path}: stale exemption").ToList();
        foreach (var (path, source) in sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (exempt.Contains(path)) continue;
            findings.AddRange(ForbiddenUses(source).Distinct(StringComparer.Ordinal).Select(use => $"{path}: {use}"));
        }
        return findings;
    }

    private static IEnumerable<string> ForbiddenUses(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var aliases = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(use => use.Alias is not null && use.Name is not null)
            .GroupBy(use => use.Alias!.Name.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name!.ToString().Replace("global::", ""), StringComparer.Ordinal);
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax or NameEqualsSyntax) continue;
            var rule = identifier.Identifier.ValueText switch
            {
                "File" or "Directory" or "FileInfo" or "FileStream" or "StreamReader" => "file-system",
                "SqliteConnection" or "SqliteOperatorIntentStore" or "PortfolioStore" => "storage",
                _ => null
            };
            if (rule is null) continue;
            // A property (entry.Directory) is not a type; retain qualified and namespace-aliased type access.
            if (identifier.Parent is MemberAccessExpressionSyntax member && member.Name == identifier)
            {
                var qualifier = member.Expression.ToString().Replace("global::", "");
                qualifier = aliases.GetValueOrDefault(qualifier, qualifier);
                var expected = rule == "file-system" ? "System.IO" : identifier.Identifier.ValueText == "SqliteConnection"
                    ? "Microsoft.Data.Sqlite" : "Mcg.AgentOrchestrator.Infrastructure";
                if (qualifier != expected) continue;
            }
            yield return $"{rule} use {identifier.Identifier.ValueText}";
        }
    }
}
