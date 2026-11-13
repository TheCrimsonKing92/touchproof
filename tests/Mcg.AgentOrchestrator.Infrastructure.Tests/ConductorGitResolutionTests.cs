using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private layouts/workspaces, fake launch seams, and AsyncLocal overrides.
public sealed class ConductorGitResolutionTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData("cmd", "git.exe", false)]
    [InlineData("CmD", "GiT.ExE", false)]
    [InlineData("mingw32/bin", "git.exe", true)]
    [InlineData("mingw64/bin", "git.exe", true)]
    [InlineData("clangarm64/bin", "git.exe", true)]
    [InlineData("MiNgW64/BiN", "GiT.ExE", true)]
    public void Classify_RecognizedLayout_IdentifiesCmdAndRawGit(
        string directory, string name, bool raw)
    {
        using var layout = ContrastLayout();
        var path = layout.Executable(directory, name);

        var result = ConductorGitResolution.Classify(path);

        Assert.Equal(raw ? ConductorGitResolution.Classification.RawMinGw :
            ConductorGitResolution.Classification.Cmd, result.Kind);
        Assert.Equal(raw, result.RefusesStart);
        Assert.Equal(path, result.ResolvedPath);
        Assert.Equal(path, result.LogValue);
        Assert.Equal(raw ? Path.Combine(layout.Root, "cmd", "git.exe") : null, result.ExpectedCmdPath);
    }

    [Theory]
    [InlineData("bin", "git.exe")]
    [InlineData("usr/bin", "git.exe")]
    [InlineData("mingw64", "git.exe")]
    [InlineData("mingw128/bin", "git.exe")]
    [InlineData("cmd", "other.exe")]
    [InlineData("mingw64/bin", "other.exe")]
    public void Classify_OtherLayout_RemainsPermitted(string directory, string name)
    {
        using var layout = ContrastLayout();
        var path = layout.Executable(directory, name);
        var result = ConductorGitResolution.Classify(path);
        Assert.Equal(ConductorGitResolution.Classification.Unrecognized, result.Kind);
        Assert.False(result.RefusesStart);
        Assert.Equal(path, result.ResolvedPath);
        Assert.Null(result.ExpectedCmdPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("git")]
    [InlineData("relative/git.exe")]
    public void Resolve_MissingExecutable_ReportsUnavailable(string? path)
    {
        using var layout = ContrastLayout();
        var result = ConductorGitResolution.Resolve(true, _ => path);
        Assert.Equal(ConductorGitResolution.Classification.Missing, result.Kind);
        Assert.False(result.RefusesStart);
        Assert.Null(result.ResolvedPath);
        Assert.Null(result.ExpectedCmdPath);
        Assert.Equal("none", result.LogValue);
    }

    [Fact]
    public void Resolve_NonWindows_DoesNotCallResolver()
    {
        using var layout = ContrastLayout();
        var calls = 0;
        var result = ConductorGitResolution.Resolve(false, _ =>
        {
            calls++;
            throw new InvalidOperationException("Non-Windows must not resolve git.");
        });
        Assert.Equal(0, calls);
        Assert.Equal(ConductorGitResolution.Classification.NotChecked, result.Kind);
        Assert.False(result.RefusesStart);
        Assert.Null(result.ResolvedPath);
        Assert.Equal("none", result.LogValue);
    }

    [Fact]
    public void Resolve_ResolverFault_RemainsPermitted()
    {
        using var layout = ContrastLayout();
        var result = ConductorGitResolution.Resolve(true, _ => throw new IOException("lookup failed"));
        Assert.Equal(ConductorGitResolution.Classification.Missing, result.Kind);
        Assert.False(result.RefusesStart);
        Assert.Null(result.ResolvedPath);
    }

    [Fact]
    public void Resolve_Windows_UsesOneLauncherResolution()
    {
        using var layout = ContrastLayout();
        var calls = 0;
        var path = layout.Executable("cmd");
        var result = ConductorGitResolution.Resolve(true, name =>
        {
            Assert.Equal("git", name);
            calls++;
            return HandleListProcessLauncher.ResolveApplication(path);
        });
        Assert.Equal(1, calls);
        Assert.Equal(path, result.ResolvedPath);
        Assert.Equal(ConductorGitResolution.Classification.Cmd, result.Kind);
    }

    [Fact]
    public void Classify_NormalizedSeparatorsAndParents_RecognizesRawGit()
    {
        using var layout = ContrastLayout();
        var raw = layout.Executable("mingw64/bin");
        var path = Path.Combine(Path.GetDirectoryName(raw)!, "..", "bin", "git.exe")
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = ConductorGitResolution.Classify(path);
        Assert.True(result.RefusesStart);
        Assert.Equal(path, result.ResolvedPath);
        Assert.Equal(Path.Combine(layout.Root, "cmd", "git.exe"), result.ExpectedCmdPath);
    }

    [Fact]
    public void Classify_RawGitAtDriveRoot_DerivesCmdWithoutSibling()
    {
        using var layout = ContrastLayout();
        var root = Path.GetPathRoot(layout.Root)!;
        var result = ConductorGitResolution.Classify(Path.Combine(root, "clangarm64", "bin", "git.exe"));
        Assert.True(result.RefusesStart);
        Assert.Equal(Path.Combine(root, "cmd", "git.exe"), result.ExpectedCmdPath);
    }

    [Fact]
    public void Classify_RawGitWithoutCmdSibling_StillRefuses()
    {
        using var contrast = ContrastLayout();
        using var layout = new GitLayout();
        var result = ConductorGitResolution.Classify(layout.Executable("clangarm64/bin"));
        var expected = Path.Combine(layout.Root, "cmd", "git.exe");
        Assert.False(File.Exists(expected));
        Assert.True(result.RefusesStart);
        Assert.Equal(expected, result.ExpectedCmdPath);
    }

    [Fact]
    public void Current_RepeatedReadsAndNestedScopes_RetainAndRestoreObservation()
    {
        using var layout = ContrastLayout();
        var process = ConductorGitResolution.Current;
        Assert.Same(process, ConductorGitResolution.Current);
        var calls = 0;
        var raw = ConductorGitResolution.Resolve(true, _ =>
        {
            calls++;
            return layout.Executable("mingw64/bin");
        });
        using (ConductorGitResolution.Use(raw))
        {
            Assert.Same(raw, ConductorGitResolution.Current);
            using (ConductorGitResolution.Use(ConductorGitResolution.NotChecked))
                Assert.Same(ConductorGitResolution.NotChecked, ConductorGitResolution.Current);
            Assert.Same(raw, ConductorGitResolution.Current);
            Assert.Equal(1, calls);
        }
        Assert.Same(process, ConductorGitResolution.Current);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Start_RawGit_RefusesBeforeAnyLaunchArtifacts(bool clearStop, bool existingStop)
    {
        using var layout = ContrastLayout();
        using var fixture = new ConductorVerbStartTests.Fixture();
        var rawPath = layout.Executable("mingw64/bin");
        var resolution = ConductorGitResolution.Classify(rawPath);
        if (existingStop) File.WriteAllText(fixture.Workspace.ConductorStopFilePath, "operator stop");
        var before = Directory.GetFiles(fixture.Workspace.RootDirectory, "*", SearchOption.AllDirectories);
        var launcher = new ConductorVerbStartTests.RecordingLauncher();
        var setupCalls = 0;
        using var standardOutput = new StringWriter();
        using var error = new StringWriter();

        var exit = CliConductorCommand.Run(clearStop ? ["conductor", "start", "--clear-stop"] :
            ["conductor", "start"], fixture.Workspace, launcher,
            new ConductorVerbStartTests.FixedProbe(null), output: standardOutput, error: error,
            storeSetup: _ => { setupCalls++; return []; }, gitResolution: resolution);

        Assert.Equal(1, exit);
        var refusal = Assert.Single(error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("Refused:", refusal, StringComparison.Ordinal);
        Assert.Contains(rawPath, refusal, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(layout.Root, "cmd", "git.exe"), refusal, StringComparison.Ordinal);
        Assert.Equal("", standardOutput.ToString());
        Assert.Empty(launcher.Requests);
        Assert.Equal(0, setupCalls);
        Assert.Equal(before.OrderBy(p => p), Directory.GetFiles(fixture.Workspace.RootDirectory,
            "*", SearchOption.AllDirectories).OrderBy(p => p));
        Assert.Equal(existingStop, File.Exists(fixture.Workspace.ConductorStopFilePath));
        if (existingStop) Assert.Equal("operator stop", File.ReadAllText(fixture.Workspace.ConductorStopFilePath));
    }

    [Fact]
    public void Start_TrappingShellAndRawGit_PreservesShellRefusalPrecedence()
    {
        using var layout = ContrastLayout();
        using var fixture = new ConductorVerbStartTests.Fixture();
        var launcher = new ConductorVerbStartTests.RecordingLauncher();
        using var error = new StringWriter();
        using var standardOutput = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new ConductorVerbStartTests.FixedProbe(null), output: standardOutput, error: error,
            jobProbe: new TrappingJobProbe(),
            gitResolution: ConductorGitResolution.Classify(layout.Executable("mingw64/bin")));
        Assert.Equal(1, exit);
        Assert.StartsWith("Refused: this shell's job", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("MinGW", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void Start_DefaultResolution_UsesSameObservationAsLoopStart()
    {
        using var layout = ContrastLayout();
        using var fixture = new ConductorVerbStartTests.Fixture();
        var calls = 0;
        var path = layout.Executable("mingw64/bin");
        var resolution = ConductorGitResolution.Resolve(true, _ => { calls++; return path; });
        using var scope = ConductorGitResolution.Use(resolution);
        using var error = new StringWriter();
        using var standardOutput = new StringWriter();
        var setupCalls = 0;
        // A reported owner prevents a real launch even if the new refusal is removed.
        var exit = CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(4242), output: standardOutput, error: error,
            storeSetup: _ => { setupCalls++; return []; }, jobProbe: ProcessJobTrapProbe.UnknownProbe);
        var log = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop().Run(
            new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
            Path.Combine(layout.Root, "stop"), maxIterations: 0));

        Assert.Equal(1, exit);
        Assert.Contains(path, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("raw MinGW", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("", standardOutput.ToString());
        Assert.Equal(0, setupCalls);
        Assert.Contains("gitPath=" + path.Replace(' ', '_'), log, StringComparison.Ordinal);
        Assert.Same(resolution, ConductorGitResolution.Current);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("usr/bin")]
    [InlineData("missing")]
    [InlineData("non-windows")]
    public void Start_PermittedGit_LaunchesNormally(string kind)
    {
        using var layout = ContrastLayout();
        using var fixture = new ConductorVerbStartTests.Fixture();
        var resolution = kind switch
        {
            "missing" => ConductorGitResolution.Resolve(true, _ => "git"),
            "non-windows" => ConductorGitResolution.Resolve(false, _ => throw new InvalidOperationException()),
            _ => ConductorGitResolution.Classify(layout.Executable(kind))
        };
        var setupCalls = 0;
        var launcher = new ConductorVerbStartTests.RecordingLauncher(() => Assert.Equal(1, setupCalls));
        using var standardOutput = new StringWriter();
        using var error = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new ConductorVerbStartTests.FixedProbe(null), output: standardOutput, error: error,
            storeSetup: _ => { setupCalls++; return []; }, gitResolution: resolution);
        Assert.Equal(0, exit);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal(OperatingSystem.IsWindows() ? ConductorVerbStartTests.ExpectedHost() : "pwsh", request.FileName);
        Assert.Equal("Conductor launched: pid 1234; stdout log: stdout.log" + Environment.NewLine +
            "Check status: touchproof.cmd conductor status" + Environment.NewLine, standardOutput.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(1, setupCalls);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("mingw64/bin")]
    [InlineData("missing")]
    [InlineData("non-windows")]
    public void LoopStart_RetainedObservation_EmitsSanitizedGitPathAndPrefix(string kind)
    {
        using var layout = ContrastLayout();
        var calls = 0;
        var path = kind is "missing" or "non-windows" ? null : layout.Executable(kind);
        var resolution = ConductorGitResolution.Resolve(kind != "non-windows", _ => { calls++; return path; });
        using var scope = ConductorGitResolution.Use(resolution);
        var log = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop().Run(
            new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
            Path.Combine(layout.Root, "stop"), maxIterations: 0));

        var start = Assert.Single(log.Split('\n'), line => line.StartsWith("LOOP_START ", StringComparison.Ordinal));
        Assert.StartsWith("LOOP_START policy=", start, StringComparison.Ordinal);
        var field = Assert.Single(start.Trim().Split(' '), token => token.StartsWith("gitPath=", StringComparison.Ordinal));
        var expected = path is null ? "none" : path.Replace(' ', '_');
        Assert.Equal("gitPath=" + expected, field);
        Assert.Contains("policySource=", start, StringComparison.Ordinal);
        Assert.Contains("maxIterations=0", start, StringComparison.Ordinal);
        Assert.Contains("effectiveWorkerCap=", start, StringComparison.Ordinal);
        Assert.Same(resolution, ConductorGitResolution.Current);
        Assert.Equal(kind == "non-windows" ? 0 : 1, calls);
        if (path is not null) Assert.True(expected.Length > 40);
    }

    // Every test exercises this contrast so inverting cmd/raw classification fails each one.
    private static GitLayout ContrastLayout()
    {
        var layout = new GitLayout();
        try
        {
            var cmd = ConductorGitResolution.Classify(layout.Executable("cmd"));
            var raw = ConductorGitResolution.Classify(layout.Executable("mingw64/bin"));
            Assert.Equal(ConductorGitResolution.Classification.Cmd, cmd.Kind);
            Assert.False(cmd.RefusesStart);
            Assert.Equal(ConductorGitResolution.Classification.RawMinGw, raw.Kind);
            Assert.True(raw.RefusesStart);
            return layout;
        }
        catch { layout.Dispose(); throw; }
    }

    private sealed class GitLayout : IDisposable
    {
        internal string Root { get; } = CreateTempDirectory("conductor git layout");
        internal string Executable(string directory, string name = "git.exe")
        {
            var parent = Path.Combine(Root, directory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(parent);
            var path = Path.Combine(parent, name);
            File.WriteAllText(path, "fixture");
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class TrappingJobProbe : IProcessJobProbe
    {
        public ProcessJobReading Read() => new(true, 0x00002000);
    }
}
