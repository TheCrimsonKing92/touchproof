using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: owned fixture roots and registry; crosswalk, measurement and forge use fakes.
public sealed class ProjectCheckManifestCliTests
{
    [Fact]
    public void Check_InstalledMeasuredModel_MatchesWithoutMeasuring()
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        var stored = ProjectModelJson.Deserialize(File.ReadAllText(fixture.ModelPath("sample")));
        var fresh = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root("sample"));
        Assert.Empty(fresh.Measurements);
        Assert.All(stored.Units.Where(unit => unit.IsTest.Value == true), unit =>
            Assert.NotNull(stored.Measurements.Single(item => item.UnitId == unit.Id).TestSeconds));
        Assert.Equal(2, stored.Units.Count(unit => unit.IsTest.Value == true));
        Assert.DoesNotContain(OwnerAnswerFile.Read(fixture.Directory("sample")),
            answer => answer.FactKey == OwnerAnswerValidator.DefaultTimeoutKey);
        Assert.NotNull(stored.DeliveryProbe);
        Assert.Contains(stored.OwnerQuestions, question => question.FactKey == ProjectDelivery.ModeKey);
        var calls = fixture.Crosswalk.Calls.Count;

        var (exit, lines) = fixture.Check(["sample"]);

        Assert.Equal(0, exit);
        Assert.Equal(["Match sample"], lines);
        Assert.Equal(calls + 1, fixture.Crosswalk.Calls.Count);
    }

    [Theory]
    [InlineData("digit")]
    [InlineData("truncate")]
    [InlineData("append")]
    [InlineData("bom")]
    [InlineData("crlf")]
    [InlineData("invalid-utf8")]
    [InlineData("long-unicode")]
    public void Check_ChangedBytes_ReportsExactLocationAndBothExcerpts(string change)
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        var learned = File.ReadAllBytes(fixture.Live("sample"));
        var text = Encoding.UTF8.GetString(learned);
        Assert.Contains("\"defaultMinutes\": 5", text);
        var digit = text.IndexOf("\"defaultMinutes\": ", StringComparison.Ordinal) + "\"defaultMinutes\": ".Length;
        Assert.True(digit > 0);
        var installed = change switch
        {
            "digit" => learned.ToArray(),
            "truncate" => learned[..^1],
            "append" => learned.Concat(new byte[] { (byte)'!' }).ToArray(),
            "bom" => new byte[] { 0xef, 0xbb, 0xbf }.Concat(learned).ToArray(),
            "crlf" => Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal)),
            "invalid-utf8" => new byte[] { 0xff }.Concat(learned[1..]).ToArray(),
            _ => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("😀", 201)))
        };
        if (change == "digit") installed[digit] = (byte)'9';
        var offset = change switch
        {
            "digit" => digit, "truncate" => learned.Length - 1, "append" => learned.Length,
            "crlf" => text.IndexOf('\n'), _ => 0
        };
        var line = 1 + text[..offset].Count(character => character == '\n');
        var installedLine = change == "long-unicode" ? string.Concat(Enumerable.Repeat("😀", 200))
            : Encoding.UTF8.GetString(installed).Split('\n').ElementAtOrDefault(line - 1)?.TrimEnd('\r') ?? "";
        var learnedLine = text.Split('\n').ElementAtOrDefault(line - 1)?.TrimEnd('\r') ?? "";
        File.WriteAllBytes(fixture.Live("sample"), installed);

        var (exit, lines) = fixture.Check(["sample"]);

        Assert.Equal(1, exit);
        Assert.Equal(new[] { $"Differs sample at byte offset {offset} line {line}",
            $"  installed: {installedLine}", $"  learned: {learnedLine}" }.Concat(change switch
            {
                "digit" => new[] { "  kind: Other", "  sections: added=- missing=- changed=engine",
                    "  checks: added=- missing=- changed=-" },
                "truncate" or "crlf" => new[] { "  kind: Other", "  sections: added=- missing=- changed=-",
                    "  checks: added=- missing=- changed=-" },
                _ => Array.Empty<string>()
            }), lines);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("invalid-schema")]
    [InlineData("unusable-measurements")]
    public void Check_UnavailableStoredModel_RefusesMissingDefaultTimeout(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        switch (scenario)
        {
            case "absent": File.Delete(fixture.ModelPath("sample")); break;
            case "malformed": File.WriteAllText(fixture.ModelPath("sample"), "{"); break;
            case "invalid-schema": File.WriteAllText(fixture.ModelPath("sample"), "{}"); break;
            default:
                var stored = ProjectModelJson.Deserialize(File.ReadAllText(fixture.ModelPath("sample")));
                File.WriteAllText(fixture.ModelPath("sample"), ProjectModelJson.Serialize(stored with { Measurements = [null!] }));
                break;
        }

        var (exit, lines) = fixture.Check(["sample"]);

        Assert.Equal(1, exit);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Blocked engine/timeouts/defaultMinutes ", lines[0]);
        Assert.StartsWith("OwnerQuestion engine/timeouts/defaultMinutes ", lines[1]);
        Assert.Equal("Refused sample 1 facts the gate needs are unanswered", lines[2]);
    }

    [Fact]
    public void Overlay_StoredMeasurements_KeepsOnlyFreshUnitIdsWithOrdinalEquality()
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        var stored = ProjectModelJson.Deserialize(File.ReadAllText(fixture.ModelPath("sample")));
        var fresh = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root("sample"));
        var measurement = stored.Measurements[0];
        stored = stored with { Measurements = [measurement,
            new("removed.csproj", measurement.BuildSeconds, measurement.TestSeconds),
            new(measurement.UnitId.ToUpperInvariant(), measurement.BuildSeconds, measurement.TestSeconds)] };

        var overlaid = ProjectLearnedManifestSource.OverlayMeasurements(fresh, stored);

        Assert.Equal(new[] { measurement }, overlaid.Measurements);
        Assert.Equal(fresh, overlaid with { Measurements = fresh.Measurements });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CheckAll_MixedRegistry_OrdersVerdictsContinuesAndSetsExit(bool differs)
    {
        using var fixture = new Fixture("alpha");
        fixture.Install("alpha");
        var expected = new List<string> { "Match alpha" };
        if (differs)
        {
            fixture.AddProject("beta");
            fixture.Install("beta");
            var bytes = File.ReadAllBytes(fixture.Live("beta"));
            bytes[0] = (byte)'!';
            File.WriteAllBytes(fixture.Live("beta"), bytes);
            expected.AddRange(["Differs beta at byte offset 0 line 1", "  installed: !", "  learned: {"]);
        }
        fixture.AddProject("gamma");
        expected.AddRange(["Skipped default", "NotInstalled gamma"]);

        var (exit, lines) = fixture.Check(["--all"]);

        Assert.Equal(differs ? 1 : 0, exit);
        Assert.Equal(expected, lines);
    }

    [Theory]
    [InlineData("sample", "--all")]
    [InlineData("--all", "sample")]
    [InlineData("--all", "--all")]
    [InlineData("sample", "sample")]
    [InlineData("--unknown")]
    [InlineData("-x")]
    [InlineData("")]
    [InlineData("unknown-project")]
    public void Check_InvalidArguments_ThrowsUsageNamingVerbWithoutWriting(params string[] selectors)
    {
        using var fixture = new Fixture();
        fixture.Check(selectors, usageError: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Check_NoSelector_UsesSelectedProjectOrOverride(bool useOverride)
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        if (!useOverride) fixture.Registry.SelectProject("sample");

        var (exit, lines) = fixture.Check([], activeProject: useOverride ? "sample" : null);

        Assert.Equal(0, exit);
        Assert.Equal(["Match sample"], lines);
    }

    [Theory]
    [InlineData("default", "Skipped default")]
    [InlineData("sample", "NotInstalled sample")]
    public void Check_SkippedOrNotInstalled_IsNotSingleProjectSuccess(string name, string verdict)
    {
        using var fixture = new Fixture();
        var (exit, lines) = fixture.Check([name], readerFactory: () => throw new InvalidOperationException("must not discover"));
        Assert.Equal(1, exit);
        Assert.Equal(new[] { verdict }, lines);
    }

    [Theory]
    [InlineData("answers")]
    [InlineData("installed")]
    public void Check_UnreadableInput_ReportsUnreadable(string input)
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        if (input == "answers") File.WriteAllText(fixture.AnswersPath("sample"), "{");
        else
        {
            File.Delete(fixture.Live("sample"));
            System.IO.Directory.CreateDirectory(fixture.Live("sample"));
        }

        var (exit, lines) = fixture.Check(["sample"]);

        Assert.Equal(1, exit);
        Assert.StartsWith("Unreadable sample ", Assert.Single(lines));
    }

    [Fact]
    public void CheckAll_DiscoveryThrows_ReportsSingleLineReasonAndContinues()
    {
        using var fixture = new Fixture("alpha");
        fixture.Install("alpha");
        fixture.AddProject("gamma");
        fixture.Install("gamma");
        var calls = 0;

        var (exit, lines) = fixture.Check(["--all"], readerFactory: () => ++calls == 1
            ? throw new InvalidOperationException("fixture\r\nfailure") : fixture.Crosswalk.Reader);

        Assert.Equal(1, exit);
        Assert.Equal(2, calls);
        Assert.Equal(["Unreadable alpha fixture  failure", "Skipped default", "Match gamma"], lines);
    }

    [Fact]
    public void Refresh_WithoutApply_PrintsClassificationAndWritesNothing()
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        fixture.RemoveEnvironmentContract();
        var expected = fixture.Check(["sample"]).Lines;
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Refresh(["sample"]);

        Assert.Equal(0, exit);
        Assert.Equal(expected, lines);
        Assert.Equal(new[] { "  kind: NewSectionsOnly",
            "  sections: added=environmentContract missing=- changed=-",
            "  checks: added=- missing=- changed=-" }, lines[^3..]);
        fixture.AssertSnapshot(before);
    }

    [Fact]
    public void Refresh_Apply_NewSectionsOnly_InstallsLearnedWithBackupAndCheckMatches()
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        var learned = File.ReadAllBytes(fixture.Live("sample"));
        fixture.RemoveEnvironmentContract();
        var installed = File.ReadAllBytes(fixture.Live("sample"));
        using var clock = ProjectInstallManifestCommand.UseClock(() => DateTimeOffset.UnixEpoch);

        var (exit, lines) = fixture.Refresh(["sample", "--apply"]);

        Assert.Equal(0, exit);
        Assert.Equal("  kind: NewSectionsOnly", lines[^4]);
        Assert.Equal("  sections: added=environmentContract missing=- changed=-", lines[^3]);
        Assert.Equal("  checks: added=- missing=- changed=-", lines[^2]);
        var backup = fixture.Live("sample") + ".bak-19700101T000000Z";
        Assert.StartsWith("Installed manifest: ", lines[^1]);
        Assert.EndsWith($"previous saved as {backup})", lines[^1]);
        Assert.Equal(learned, File.ReadAllBytes(fixture.Live("sample")));
        Assert.Equal(backup, Assert.Single(System.IO.Directory.GetFiles(fixture.Directory("sample"), "*.bak-*")));
        Assert.Equal(installed, File.ReadAllBytes(backup));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory("sample"), "*.tmp"));
        var check = fixture.Check(["sample"]);
        Assert.Equal(0, check.Exit);
        Assert.Equal(["Match sample"], check.Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_Apply_OtherDifference_RefusesAndLeavesInstalled(bool apply)
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        var text = File.ReadAllText(fixture.Live("sample"));
        Assert.Contains("\"defaultMinutes\": 5", text);
        File.WriteAllText(fixture.Live("sample"), text.Replace("\"defaultMinutes\": 5", "\"defaultMinutes\": 9", StringComparison.Ordinal));
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Refresh(apply ? ["sample", "--apply"] : ["sample"]);

        Assert.Equal(apply ? 1 : 0, exit);
        Assert.Contains("  kind: Other", lines);
        if (apply) Assert.StartsWith("Refused: ", lines[^1]);
        fixture.AssertSnapshot(before);
    }

    [Fact]
    public void Refresh_Apply_DeclaredCheckOnlyInLearned_RefusesNamingCheckUnderAdded()
    {
        using var fixture = new Fixture(declared: true);
        fixture.InstallDeclared("none");
        fixture.SetBuildAnswer("node build.js");
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Refresh(["sample", "--apply"]);

        Assert.Equal(1, exit);
        Assert.Contains("  kind: ChecksChanged", lines);
        Assert.Contains("  checks: added=declared-build missing=- changed=-", lines);
        Assert.StartsWith("Refused: ", lines[^1]);
        fixture.AssertSnapshot(before);
    }

    [Fact]
    public void Refresh_Apply_DeclaredCheckOnlyInInstalled_RefusesNamingCheckUnderMissing()
    {
        using var fixture = new Fixture(declared: true);
        fixture.InstallDeclared("node build.js");
        fixture.SetBuildAnswer("none");
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Refresh(["--apply", "sample"]);

        Assert.Equal(1, exit);
        Assert.Contains("  kind: ChecksChanged", lines);
        Assert.Contains("  checks: added=- missing=declared-build changed=-", lines);
        Assert.StartsWith("Refused: ", lines[^1]);
        fixture.AssertSnapshot(before);
    }

    [Fact]
    public void Refresh_UnknownVerbUsage_ListsRefreshManifest()
    {
        using var fixture = new Fixture();
        var exception = Assert.Throws<ArgumentException>(() => fixture.Execute(["project", "unknown-verb"]));
        Assert.Contains("refresh-manifest [name] [--apply]", exception.Message);
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("--unknown")]
    [InlineData("sample", "sample")]
    [InlineData("--apply", "--apply")]
    [InlineData("unknown-project")]
    [InlineData("")]
    public void Refresh_InvalidArguments_ThrowsUsageWithoutWriting(params string[] selectors)
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var exception = Assert.Throws<ArgumentException>(() => fixture.Refresh(selectors));
        Assert.Contains(ProjectRefreshManifestCommand.Usage, exception.Message);
        fixture.AssertSnapshot(before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Refresh_Match_UsesSelectedProjectOrOverrideAndWritesNothing(bool apply, bool useOverride)
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        if (!useOverride) fixture.Registry.SelectProject("sample");
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Refresh(apply ? ["--apply"] : [], useOverride ? "sample" : null);

        Assert.Equal(0, exit);
        Assert.Equal(["Match sample"], lines);
        fixture.AssertSnapshot(before);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("answers")]
    [InlineData("installed")]
    [InlineData("unclassifiable")]
    public void Refresh_Apply_UnavailableComparison_RefusesWithoutWriting(string input)
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        if (input == "blocked") File.Delete(fixture.ModelPath("sample"));
        else if (input == "answers") File.WriteAllText(fixture.AnswersPath("sample"), "{");
        else if (input == "unclassifiable") File.WriteAllText(fixture.Live("sample"), "{");
        else
        {
            File.Delete(fixture.Live("sample"));
            System.IO.Directory.CreateDirectory(fixture.Live("sample"));
        }
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Refresh(["sample", "--apply"]);

        Assert.Equal(1, exit);
        if (input == "blocked")
        {
            Assert.StartsWith("Blocked engine/timeouts/defaultMinutes ", lines[0]);
            Assert.Equal("Refused sample 1 facts the gate needs are unanswered", lines[^1]);
        }
        else if (input == "unclassifiable")
            Assert.Equal("Refused: the difference could not be classified", lines[^1]);
        else Assert.StartsWith("Unreadable sample ", Assert.Single(lines));
        fixture.AssertSnapshot(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_Apply_BackupCollision_RefusesWithoutWriting(bool directory)
    {
        using var fixture = new Fixture();
        fixture.Install("sample");
        fixture.RemoveEnvironmentContract();
        using var clock = ProjectInstallManifestCommand.UseClock(() => DateTimeOffset.UnixEpoch);
        var backup = fixture.Live("sample") + ".bak-19700101T000000Z";
        if (directory) System.IO.Directory.CreateDirectory(backup);
        else File.WriteAllText(backup, "preserved backup");
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Refresh(["sample", "--apply"]);

        Assert.Equal(1, exit);
        Assert.Equal($"Refused: backup already exists {backup}", lines[^1]);
        fixture.AssertSnapshot(before);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ProjectOnboardingFixture home = new("no-solution");
        private readonly ProjectOnboardingFixture scratch = new("no-solution");
        private readonly Dictionary<string, ProjectOnboardingFixture> sources = new(StringComparer.Ordinal);
        public FakeCrosswalkProcess Crosswalk { get; }
        public OrchestratorProjectRegistry Registry { get; }
        public Fixture(string name = "sample", bool declared = false)
        {
            Crosswalk = new(declared ? "unknown" : "dotnet");
            Registry = new(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
            AddProject(name, declared);
        }
        public void AddProject(string name, bool declared = false)
        {
            var source = new ProjectOnboardingFixture(declared ? "declared-no-marker" : "answer-key-repository");
            if (declared) ProjectOnboardingFixture.SeedRemoteDefault(source.Root);
            sources.Add(name, source);
            Registry.CreateProject(name, source.Root);
        }
        public string Root(string name) => sources[name].Root;
        public string Directory(string name) => Registry.GetRequiredProject(name).ResolveWorkspace().OrchestratorDirectory;
        public string ModelPath(string name) => Path.Combine(Directory(name), "project-model.json");
        public string AnswersPath(string name) => Path.Combine(Directory(name), OwnerAnswerFile.FileName);
        public string Live(string name) => Path.Combine(Directory(name), "acceptance-manifest.json");
        public void Install(string name)
        {
            var measurer = new RecordingUnitCommandMeasurer();
            Assert.Equal(0, Execute(["project", "discover", name, "--measure", "all"], measurer).Exit);
            Assert.Equal(2, measurer.Calls.Count(call => call.Kind == UnitCommandKinds.Test));
            foreach (var (key, value) in new[] { ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
                Assert.Equal(0, Execute(["project", "answer", name, "engine/timeouts/" + key, value]).Exit);
            var emitted = Path.Combine(scratch.Root, name + ".json");
            Assert.Equal(0, Execute(["project", "emit-manifest", name, "--out", emitted]).Exit);
            Assert.Equal(0, Execute(["project", "install-manifest", name, "--from", emitted]).Exit);
        }
        private (int Exit, string[] Lines) Execute(string[] parts, IUnitCommandMeasurer measurer)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, Registry, home.Root, null, output,
                measurerFactory: () => measurer, pullRequestProbeFactory: () => new RequiredProbe(),
                crosswalkReaderFactory: () => Crosswalk.Reader);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        public (int Exit, string[] Lines) Execute(string[] parts) => Execute(parts, new RecordingUnitCommandMeasurer());
        public (int Exit, string[] Lines) Check(string[] selectors, string? activeProject = null,
            bool usageError = false, Func<CrosswalkStackReader>? readerFactory = null)
        {
            var before = Snapshot();
            using var output = new StringWriter();
            try
            {
                int Invoke() => ProjectCliCommand.Execute(["project", "check-manifest", .. selectors],
                    Registry, home.Root, activeProject, output,
                    measurerFactory: () => throw new InvalidOperationException("check-manifest must never construct a measurer"),
                    crosswalkReaderFactory: readerFactory ?? (() => Crosswalk.Reader));
                if (usageError)
                {
                    var exception = Assert.Throws<ArgumentException>(() => Invoke());
                    Assert.Contains("Usage: project check-manifest [name|--all]", exception.Message);
                    Assert.Empty(output.ToString());
                    return (1, []);
                }
                var exit = Invoke();
                return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            }
            finally
            {
                var after = Snapshot();
                Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
                foreach (var (path, state) in before) Assert.Equal(state, after[path]);
            }
        }
        public void InstallDeclared(string build)
        {
            Assert.Equal(0, Execute(["project", "discover", "sample"]).Exit);
            var model = ProjectModelJson.Deserialize(File.ReadAllText(ModelPath("sample")));
            Assert.Equal(ProjectStack.Declared, model.Stack);
            foreach (var (key, value) in new[] {
                (OwnerAnswerValidator.DefaultTimeoutKey, "5"),
                (OwnerAnswerValidator.BuildServerShutdownKey, "3"),
                (OwnerAnswerValidator.DiscoveryKey, "4"),
                (DeclaredProjectDiscoveryAdapter.BuildQuestion, build),
                (DeclaredProjectDiscoveryAdapter.TestQuestion, "node --test --test-reporter=junit"),
                (DeclaredProjectDiscoveryAdapter.ReportsQuestion, "reports/*.xml") })
                Assert.Equal(0, Execute(["project", "answer", "sample", key, value]).Exit);
            var emitted = Path.Combine(scratch.Root, "declared.json");
            Assert.Equal(0, Execute(["project", "emit-manifest", "sample", "--out", emitted]).Exit);
            Assert.Equal(0, Execute(["project", "install-manifest", "sample", "--from", emitted]).Exit);
            Assert.Equal(0, Check(["sample"]).Exit);
        }
        public void SetBuildAnswer(string build)
        {
            var answers = OwnerAnswerFile.Read(Directory("sample"));
            Assert.Single(answers, answer => answer.FactKey == DeclaredProjectDiscoveryAdapter.BuildQuestion);
            OwnerAnswerFile.Write(Directory("sample"), answers.Select(answer =>
                answer.FactKey == DeclaredProjectDiscoveryAdapter.BuildQuestion
                    ? new OwnerAnswer(answer.FactKey, build, "fixture", DateTimeOffset.UnixEpoch) : answer).ToArray());
        }
        public void RemoveEnvironmentContract()
        {
            var text = File.ReadAllText(Live("sample"));
            var start = text.LastIndexOf(",\n  \"environmentContract\":", StringComparison.Ordinal);
            Assert.True(start > 0, "emitted manifest must end with environmentContract");
            File.WriteAllText(Live("sample"), text[..start] + "\n}\n");
        }
        public (int Exit, string[] Lines) Refresh(string[] selectors, string? activeProject = null)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(["project", "refresh-manifest", .. selectors],
                Registry, home.Root, activeProject, output,
                measurerFactory: () => throw new InvalidOperationException("refresh must never construct a measurer"),
                crosswalkReaderFactory: () => Crosswalk.Reader);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        public void AssertSnapshot(Dictionary<string, string> before)
        {
            var after = Snapshot();
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, state) in before) Assert.Equal(state, after[path]);
        }
        public Dictionary<string, string> Snapshot() => sources.Values.Select(source => source.Root)
            .Concat([home.Root, scratch.Root, Path.GetDirectoryName(Crosswalk.Binary)!])
            .SelectMany(root => System.IO.Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
            .ToDictionary(path => path, path => System.IO.Directory.Exists(path) ? "directory"
                : $"{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
        public void Dispose()
        {
            Crosswalk.Dispose();
            foreach (var source in sources.Values) source.Dispose();
            scratch.Dispose();
            home.Dispose();
        }
    }

    private sealed class RequiredProbe : IPullRequestRequirementProbe
    {
        public PullRequestRequirement Probe(string repositoryRoot, string branch) => PullRequestRequirement.Required("fixture");
    }
}
