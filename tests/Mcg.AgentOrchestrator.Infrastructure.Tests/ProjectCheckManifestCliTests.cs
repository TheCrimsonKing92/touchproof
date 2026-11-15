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

        var overlaid = ProjectCheckManifestCommand.OverlayMeasurements(fresh, stored);

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

    private sealed class Fixture : IDisposable
    {
        private readonly ProjectOnboardingFixture home = new("no-solution");
        private readonly ProjectOnboardingFixture scratch = new("no-solution");
        private readonly Dictionary<string, ProjectOnboardingFixture> sources = new(StringComparer.Ordinal);
        public FakeCrosswalkProcess Crosswalk { get; } = new("dotnet");
        public OrchestratorProjectRegistry Registry { get; }
        public Fixture(string name = "sample")
        {
            Registry = new(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
            AddProject(name);
        }
        public void AddProject(string name)
        {
            var source = new ProjectOnboardingFixture("answer-key-repository");
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
        private (int Exit, string[] Lines) Execute(string[] parts) => Execute(parts, new RecordingUnitCommandMeasurer());
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
        private Dictionary<string, string> Snapshot() => sources.Values.Select(source => source.Root)
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
