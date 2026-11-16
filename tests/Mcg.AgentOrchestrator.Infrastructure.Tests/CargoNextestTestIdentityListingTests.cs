using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using Listing = Mcg.AgentOrchestrator.App.Orchestration.CargoNextestTestIdentityListing;

// Parallel-safe: private manifest directories and injected runners; never starts cargo.
public sealed class CargoNextestTestIdentityListingTests
{
    // Recorded TestListSummary shape from https://nexte.st/docs/machine-readable/list/,
    // reduced to the relevant fields and populated with the incident's binary/test names.
    private const string RecordedList = """
        {
          "rust-build-meta":{"target-directory":"C:/fixture/target"},
          "test-count":2,
          "rust-suites":{
            "process-inspector":{
              "binary-id":"process-inspector","status":"listed",
              "testcases":{
                "tree::tests::groups_children_sorted":{"ignored":false,"filter-match":{"status":"matches"}}
              }
            },
            "process-inspector::tree_cli":{
              "binary-id":"process-inspector::tree_cli","status":"listed",
              "testcases":{
                "pid_json_lists_spawned_child":{"ignored":true,"filter-match":{"status":"matches"}}
              }
            }
          }
        }
        """;

    [Fact]
    public void RecordedList_CompleteOutput_ReturnsExactIdentitiesAndNames()
    {
        using var fixture = new Fixture();
        var listing = fixture.Read((_, _) => new(0, RecordedList));

        Assert.Equal(new RunnerTestIdentity[]
        {
            new("process-inspector::tree::tests::groups_children_sorted", "tree::tests::groups_children_sorted"),
            new("process-inspector::tree_cli::pid_json_lists_spawned_child", "pid_json_lists_spawned_child")
        }, listing);
        Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void CommandFailure_ValidOutput_ReturnsNoListing(int exitCode, bool timedOut)
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Read((_, _) => new(exitCode, RecordedList, timedOut)));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public void SpawnFailure_Source_ReturnsNoListing()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Read((_, _) => throw new System.ComponentModel.Win32Exception("cargo missing")));
        Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("{\"test-count\":0,\"rust-suites\":[]}")]
    [InlineData("{\"test-count\":1,\"rust-suites\":{\"suite\":{\"binary-id\":\"suite\",\"status\":\"listed\",\"testcases\":[]}}}")]
    [InlineData("{\"test-count\":1,\"rust-suites\":{\"suite\":{\"status\":\"listed\",\"testcases\":{}}}}")]
    [InlineData("{\"test-count\":1,\"rust-suites\":{}}")]
    public void MalformedOrIncompleteOutput_Source_ReturnsNoListing(string output)
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Read((_, _) => new(0, output)));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public void MalformedSiblingSuite_ValidFragment_DiscardsEntireListing()
    {
        using var fixture = new Fixture();
        var malformed = RecordedList.Replace("\"binary-id\":\"process-inspector::tree_cli\",\"status\":\"listed\"",
            "\"binary-id\":\"process-inspector::tree_cli\",\"status\":\"skipped\"", StringComparison.Ordinal);
        Assert.Null(fixture.Read((_, _) => new(0, malformed)));
    }

    [Fact]
    public void EmptyCatalog_Source_ReturnsNoListing()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Read((_, _) => new(0, "{\"test-count\":0,\"rust-suites\":{}}")));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public void DeclaredFilters_ListCommand_KeepsBuildConfigurationOnly()
    {
        using var fixture = new Fixture();
        fixture.Write(CheckFor("tests", ["nextest", "run", "--manifest-path", "Cargo.toml", "--workspace",
            "-p", "process-inspector", "--package=library", "--features", "one,two", "--all-features",
            "--no-default-features", "--profile", "ci", "--cargo-profile", "release", "--target", "host",
            "name_filter", "-E", "test(foo)", "--filterset=test(bar)", "--skip", "ignored", "--exact",
            "--retries", "2", "--junit", "report.xml", "--no-fail-fast", "--", "ignored-filter"], "rust"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "rust"));
        var observed = false;

        Assert.NotNull(fixture.Read((command, timeout) =>
        {
            observed = true;
            Assert.Equal("cargo", command.Executable);
            Assert.Equal(Path.Combine(fixture.Root, "rust"), command.WorkingDirectory);
            Assert.Equal(new[] { "nextest", "list", "--message-format", "json", "--list-type", "full",
                "--manifest-path", "Cargo.toml", "--workspace", "-p", "process-inspector", "--package", "library",
                "--features", "one,two", "--all-features", "--no-default-features", "--profile", "ci",
                "--cargo-profile", "release", "--target", "host" }, command.Arguments);
            Assert.True(timeout > TimeSpan.Zero && timeout <= Listing.ListingTimeout);
            return new(0, RecordedList);
        }));
        Assert.True(observed);
    }

    [Theory]
    [InlineData("--unknown-option")]
    [InlineData("--features")]
    [InlineData("--features=")]
    public void UnsupportedArguments_Source_DoesNotInvokeCommand(string option)
    {
        using var fixture = new Fixture();
        fixture.Write(CheckFor("tests", ["nextest", "run", option]));
        Assert.Null(fixture.Read((_, _) => throw new InvalidOperationException("must not start")));
        Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("missing")]
    [InlineData("C:\\absolute")]
    public void InvalidProjectDirectory_Source_DoesNotInvokeCommand(string project)
    {
        using var fixture = new Fixture();
        fixture.Write(CheckFor("tests", project: project));
        Assert.Null(fixture.Read((_, _) => throw new InvalidOperationException("must not start")));
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public void MultipleNextestChecks_Source_UnionsAndDeduplicates()
    {
        using var fixture = new Fixture();
        fixture.Write(CheckFor("first"), CheckFor("second"), CheckFor("third"),
            new() { Name = "other", Command = "npm", Arguments = ["test"] });
        var listing = fixture.Read((_, _) => new(0, fixture.Calls == 2
            ? RecordedList.Replace("process-inspector", "other-crate", StringComparison.Ordinal) : RecordedList));

        Assert.Equal(3, fixture.Calls);
        Assert.NotNull(listing);
        Assert.Equal(4, listing.Count);
        Assert.Contains(new("other-crate::tree_cli::pid_json_lists_spawned_child", "pid_json_lists_spawned_child"), listing);
        Assert.Contains(new("process-inspector::tree_cli::pid_json_lists_spawned_child", "pid_json_lists_spawned_child"), listing);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    public void FailedConstituent_ValidEarlierListing_DiscardsEntireCatalog(string failure)
    {
        using var fixture = new Fixture();
        fixture.Write(CheckFor("first"), CheckFor("second", failure == "unsupported"
            ? ["nextest", "run", "--unknown"] : null));
        Assert.Null(fixture.Read((_, _) => fixture.Calls == 1 ? new(0, RecordedList) : failure switch
        {
            "exit" => new(1, RecordedList),
            "timeout" => new(0, RecordedList, true),
            _ => new(0, "broken")
        }));
        Assert.Equal(failure == "unsupported" ? 1 : 2, fixture.Calls);
    }

    [Theory]
    [InlineData("dispatch")]
    [InlineData("manifest")]
    [InlineData("malformed")]
    [InlineData("no-nextest")]
    public void UnavailableContext_Source_DoesNotInvokeCommand(string unavailable)
    {
        using var fixture = new Fixture(dispatch: unavailable != "dispatch");
        if (unavailable == "manifest") File.Delete(fixture.Manifest);
        if (unavailable == "malformed") File.WriteAllText(fixture.Manifest, "broken");
        if (unavailable == "no-nextest") fixture.Write(new Check { Command = "npm", Arguments = ["test"] });
        Assert.Null(fixture.Read((_, _) => throw new InvalidOperationException("must not start")));
        Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public void DeclaredEnvironment_Source_PassesInputsToCommand()
    {
        using var fixture = new Fixture();
        fixture.Write(new Check
        {
            Type = "junit-command", Command = "cargo", Arguments = ["nextest", "run"],
            Environment = new Dictionary<string, string> { ["FEATURE_MODE"] = "fixture" }
        });
        Assert.NotNull(fixture.Read((command, _) =>
        {
            Assert.Equal("fixture", command.Environment!["FEATURE_MODE"]);
            return new(0, RecordedList);
        }));
        Assert.Equal(1, fixture.Calls);
    }

    private static Check CheckFor(string name, string[]? arguments = null, string? project = ".") =>
        new() { Name = name, Type = "command", Command = "cargo", Arguments = arguments ?? ["nextest", "run"], Project = project };

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-nextest-list-" + Guid.NewGuid().ToString("N"));
        internal string Manifest => Path.Combine(Root, "config", "acceptance-manifest.json");
        private AgentOrchestratorKernel Kernel { get; } = new();
        private TaskSpec Planner { get; } = new(TaskId.New(), "Plan", AgentRole.Planner);
        private Goal Goal { get; }
        internal int Calls { get; private set; }

        internal Fixture(bool dispatch = true)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            Write(CheckFor("tests"));
            Goal = Kernel.CreateGoal("List project tests", [Planner]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            if (dispatch) Kernel.RecordTaskDispatch(Goal.Id, Planner.Id,
                new TaskDispatchRecord("planner", "plan", Root, DateTimeOffset.UtcNow));
        }

        internal void Write(params Check[] checks) => File.WriteAllText(Manifest,
            JsonSerializer.Serialize(new { checks }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }));

        internal IReadOnlyList<RunnerTestIdentity>? Read(Func<Listing.Command, TimeSpan, Listing.Result> run) =>
            Listing.For(null, (command, timeout) => { Calls++; return run(command, timeout); })(Goal, Planner);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
