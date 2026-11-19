using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace, SQLite backlog and any CLI child.
public sealed class CliBacklogBundleFilingTests : CliCommandTestBase
{
    private const string Bundle = "Wanted:\n(1) Add a parser\n2. Persist the result\n3) Show the result";
    private const string Reason = "All three changes share a release boundary";
    private readonly string root = CreateTempDirectory();
    private readonly AgentOrchestratorKernel kernel = new();

    private protected override bool UsesGit => false;

    [Fact]
    public async Task AddBundle_RequiresReasonBeforeInserting()
    {
        var (workspace, store) = OpenBacklog();
        var bodyPath = Path.Combine(root, "bundled work.md");
        await File.WriteAllTextAsync(bodyPath, Bundle);
        string[] args = ["backlog-add", "Bundled work", "--body-file", bodyPath, "--no-similar"];

        var refused = await RunCliAsync(workspace, args);

        Assert.NotEqual(0, refused.ExitCode);
        AssertBundleRefusal(refused.StandardError);
        Assert.Empty(await store.ListAsync(includeAll: true));

        var accepted = await RunCliAsync(workspace, [.. args, "--bundled-reason", $"  {Reason}  "]);

        Assert.True(accepted.ExitCode == 0, accepted.StandardError);
        var item = Assert.Single(await store.ListAsync(includeAll: true));
        Assert.Contains($"Added: [{item.Id}]", accepted.StandardOutput);
        Assert.Equal("Bundled work", item.Title);
        Assert.Equal(Bundle, item.Body);
        Assert.Equal(Reason, item.BundledReason);
    }

    [Fact]
    public async Task UpdateBundle_RequiresFreshReasonAndPreservesItemOnRefusal()
    {
        var (workspace, store) = OpenBacklog();
        var item = await store.AddAsync("Original title", "Original body", bundledReason: "Earlier reason");
        var before = JsonSerializer.Serialize(await store.GetByExactIdAsync(item.Id));
        string[] args = ["backlog-update", item.Id[..8], "--description", Bundle, "--title", "Replacement title"];

        var error = Assert.Throws<ArgumentException>(() => Execute(workspace, args));

        AssertBundleRefusal(error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(await store.GetByExactIdAsync(item.Id)));

        Execute(workspace, [.. args, "--bundled-reason", $"  {Reason}  "]);

        var replaced = await store.GetByExactIdAsync(item.Id);
        Assert.NotNull(replaced);
        Assert.Equal("Replacement title", replaced.Title);
        Assert.Equal(Bundle, replaced.Body);
        Assert.Equal(Reason, replaced.BundledReason);

        Execute(workspace, ["backlog-update", item.Id[..8], "--title", "Metadata-only edit"]);

        var renamed = await store.GetByExactIdAsync(item.Id);
        Assert.NotNull(renamed);
        Assert.Equal("Metadata-only edit", renamed.Title);
        Assert.Equal(Bundle, renamed.Body);
        Assert.Equal(Reason, renamed.BundledReason);

        Execute(workspace, ["backlog-update", item.Id[..8], "--description", "Wanted:\n1. One slice"]);

        var singlePart = await store.GetByExactIdAsync(item.Id);
        Assert.NotNull(singlePart);
        Assert.Equal("Wanted:\n1. One slice", singlePart.Body);
        Assert.Equal("", singlePart.BundledReason);
    }

    [Theory]
    [InlineData("Wanted:\n1. One slice")]
    [InlineData("# Details\n1. First outside\n2. Second outside")]
    [InlineData("# Context\n1. First before\n2. Second before\n# Wanted\n1. One slice\n# Constraints\n1. First after\n2. Second after")]
    public async Task AddWithoutBundle_StoresEmptyReason(string body)
    {
        var (workspace, store) = OpenBacklog();

        Execute(workspace, ["backlog-add", "Example", body, "--no-similar"]);

        var item = Assert.Single(await store.ListAsync(includeAll: true));
        Assert.Equal(body, item.Body);
        Assert.Equal("", item.BundledReason);
    }

    public override async ValueTask DisposeAsync()
    {
        try { SharedTestSupport.RemoveTempDirectory(root); }
        finally { await base.DisposeAsync(); }
    }

    private static void AssertBundleRefusal(string error)
    {
        Assert.Contains("Add a parser", error);
        Assert.Contains("Persist the result", error);
        Assert.Contains("Show the result", error);
        Assert.Contains("one item per independently landable part", error);
        Assert.Contains("--bundled-reason", error);
    }

    private (OrchestratorWorkspace Workspace, BacklogStore Store) OpenBacklog()
    {
        var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), Path.Combine(root, "data"));
        var workspace = OrchestratorWorkspace.ForDirectory(root, null, null, registry);
        return (workspace, new BacklogStore(workspace.BacklogStorePath));
    }

    private void Execute(OrchestratorWorkspace workspace, string[] args) =>
        ExecuteCliAndCapture(CliArgumentParser.NormalizeArgs(args), kernel, workspace);

    private Task<CliChildProcessResult> RunCliAsync(OrchestratorWorkspace workspace, string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        string[] boundArgs = ["--repository-root", root, "--expected-state-directory",
            workspace.OrchestratorDirectory, "--disposable-fixture", .. args];
        foreach (var arg in boundArgs)
            start.ArgumentList.Add(arg);

        start.Environment.Clear();
        foreach (var name in new[]
        {
            "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA",
            "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT(x86)"
        })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(root, "registry");
        start.Environment[OrchestratorDataRoot.EnvironmentVariable] = Path.Combine(root, "data");
        start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        return CliChildProcessRunner.RunAsync(start);
    }
}
