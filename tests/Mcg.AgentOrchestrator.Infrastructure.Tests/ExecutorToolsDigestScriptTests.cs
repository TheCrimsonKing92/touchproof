using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Real PowerShell contract: child environment and all output paths belong to the fixture.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ExecutorToolsDigestScriptTests : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public async Task Script_TopLevelScripts_WritesMatchingDigestAndOrderedArray(int count)
    {
        var tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(Path.Combine(tools, "bin"));
        File.WriteAllBytes(Path.Combine(tools, "Z.PS1"), [0xEF, 0xBB, 0xBF, 13, 10]);
        if (count == 2) File.WriteAllBytes(Path.Combine(tools, "a.ps1"), [0, 1, 10]);
        File.WriteAllText(Path.Combine(tools, "notes.txt"), "ignored");
        File.WriteAllText(Path.Combine(tools, "x.ps1x"), "ignored");
        File.WriteAllText(Path.Combine(tools, "bin", "inner.ps1"), "ignored");
        var output = Path.Combine(_root, "digest.json");
        File.WriteAllText(output, "stale output");
        var result = await Run(tools, output);
        Assert.True(result.ExitCode == 0, result.Stderr);
        var bytes = File.ReadAllBytes(output);
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        var text = File.ReadAllText(output);
        var expected = ExecutorToolsDigest.Compute(tools);
        Assert.NotNull(expected);
        Assert.Equal(expected, ExecutorToolsDigest.ReadManifest(text));
        using var json = JsonDocument.Parse(text);
        var files = json.RootElement.GetProperty("files");
        Assert.Equal(JsonValueKind.Array, files.ValueKind);
        Assert.Equal(count == 2 ? new[] { "a.ps1", "z.ps1" } : new[] { "z.ps1" },
            files.EnumerateArray().Select(file => file.GetString()));
        Assert.True(DateTimeOffset.TryParse(json.RootElement.GetProperty("utc").GetString(), out _));
        Assert.False(File.Exists(output + ".tmp"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Script_MissingOrEmptyFolder_FailsWithoutManifest(bool exists)
    {
        var tools = Path.Combine(_root, "tools");
        if (exists) Directory.CreateDirectory(tools);
        var output = Path.Combine(_root, "digest.json");
        var result = await Run(tools, output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Stderr));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".tmp"));
    }

    [Xunit.Fact]
    public void RepositoryTools_TrackedRuntimeAndDigestScript_ArePresent()
    {
        var tools = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "executor", "tools");
        Assert.NotNull(ExecutorToolsDigest.Compute(tools));
        Assert.True(File.Exists(Path.Combine(tools, "Get-ExecutorToolsDigest.ps1")));
        Assert.True(File.Exists(Path.Combine(tools, "Invoke-QueuedLanes.ps1")));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Retention_EntriesAroundEachWindow_RemovesOnlyExpiredAndKeepsCancelledLog(bool custom)
    {
        SeedBareRepository();
        var oldRun = AgedFolder("runs/111111111", 60);
        var newRun = AgedFolder("runs/222222222", 0.5);
        var middleRun = AgedFolder("runs/333333333", 6);
        var unrelatedRun = AgedFolder("runs/unrelated-folder", 60);
        var oldResult = AgedFolder("results/111111111/lane", 60);
        var newResult = AgedFolder("results/222222222/lane", 0.5);
        var middleResult = AgedFolder("results/333333333/lane", 20);
        var oldJob = AgedFile("queue/taken/old.json", 60);
        var oldCancel = AgedFile("queue/taken/old.cancel", 60);
        var newJob = AgedFile("queue/taken/new.json", 0.5);
        var newCancel = AgedFile("queue/taken/new.cancel", 0.5);
        var middleJob = AgedFile("queue/taken/middle.json", 7);
        var middleCancel = AgedFile("queue/taken/middle.cancel", 7);
        var cancelledLog = AgedFile("queue/taken/cancelled.log", 60);
        var error = AgedFile("queue/taken/old.error.txt", 60);
        var queued = AgedFile("queue/pending.json", 60);
        var logTime = File.GetLastWriteTimeUtc(cancelledLog);

        var result = await RunRetention(custom
            ? ["-RunFolderDays", "10", "-TakenDays", "2", "-ResultDays", "30"] : []);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.False(Directory.Exists(oldRun));
        Assert.True(Directory.Exists(newRun));
        Assert.Equal(custom, Directory.Exists(middleRun));
        Assert.True(Directory.Exists(unrelatedRun));
        Assert.False(Directory.Exists(oldResult));
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldResult)));
        Assert.True(Directory.Exists(newResult));
        Assert.Equal(custom, Directory.Exists(middleResult));
        Assert.False(File.Exists(oldJob));
        Assert.False(File.Exists(oldCancel));
        Assert.True(File.Exists(newJob));
        Assert.True(File.Exists(newCancel));
        Assert.Equal(!custom, File.Exists(middleJob));
        Assert.Equal(!custom, File.Exists(middleCancel));
        Assert.Equal("fixture payload", File.ReadAllText(cancelledLog));
        Assert.Equal(logTime, File.GetLastWriteTimeUtc(cancelledLog));
        Assert.True(File.Exists(error));
        Assert.True(File.Exists(queued));
        Assert.False(File.Exists(Path.Combine(_root, "runner-state.json")));
        Assert.Contains(custom
            ? "retention summary: runs removed=1 window=10d; taken removed=4 window=2d; results removed=1 window=30d; failed=0"
            : "retention summary: runs removed=2 window=3d; taken removed=2 window=14d; results removed=2 window=14d; failed=0",
            OutputLines(result.Stdout));
        foreach (var path in new[] { oldRun, oldResult, oldJob, oldCancel })
            Assert.Contains(OutputLines(result.Stdout), line => line.StartsWith("retention removed ") && line.EndsWith(path));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Retention_RunningJobCommit_KeepsItsFoldersAndRemovesOtherCommit(bool prefixInName)
    {
        SeedBareRepository();
        const string prefix = "a1b2c3d4e";
        var name = prefixInName ? "attempt-A1B2C3D4E-lane" : "goal-gate-20261115-0123456789-lane";
        var keptRun = AgedFolder("runs/" + prefix, 60);
        var keptResult = AgedFolder("results/" + prefix + "/lane", 60);
        var expiredRun = AgedFolder("runs/123456789", 60);
        var expiredResult = AgedFolder("results/123456789/lane", 60);
        var cancel = AgedFile("queue/taken/" + name + ".cancel", 60);
        var jobPath = Path.Combine(_root, "queue", "taken", name + ".json");
        if (!prefixInName)
        {
            File.WriteAllText(jobPath, JsonSerializer.Serialize(new { sha = prefix + new string('f', 31) }));
            File.SetLastWriteTimeUtc(jobPath, DateTime.UtcNow.AddDays(-60));
        }
        File.WriteAllText(Path.Combine(_root, "runner-state.json"),
            JsonSerializer.Serialize(new { utc = DateTime.UtcNow, running = new[] { name }, queued = Array.Empty<string>() }));

        var result = await RunRetention();

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.True(Directory.Exists(keptRun));
        Assert.True(Directory.Exists(keptResult));
        Assert.False(Directory.Exists(expiredRun));
        Assert.False(Directory.Exists(expiredResult));
        Assert.True(File.Exists(cancel));
        Assert.Equal(!prefixInName, File.Exists(jobPath));
    }

    [Xunit.Fact]
    public async Task Retention_NoBareRepository_FallsBackAndNamesUndeletableFolder()
    {
        Assert.False(Directory.Exists(Path.Combine(_root, "repo", "repo.git")));
        var first = AgedFolder("runs/111111111", 60);
        var locked = AgedFolder("runs/222222222", 60);
        var last = AgedFolder("runs/333333333", 60);
        var taken = AgedFile("queue/taken/old.json", 60);
        var resultFolder = AgedFolder("results/111111111/lane", 60);
        // The fixture's own open handle deterministically prevents deletion on Windows.
        using var held = new FileStream(Path.Combine(locked, "payload.txt"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await RunRetention();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(first));
        Assert.True(Directory.Exists(locked));
        Assert.False(Directory.Exists(last));
        Assert.False(File.Exists(taken));
        Assert.False(Directory.Exists(resultFolder));
        var lines = OutputLines(result.Stdout);
        Assert.Contains(lines, line => line.StartsWith("retention failed ") && line.Contains(locked));
        Assert.Contains(lines, line => line.StartsWith("retention failed prune:"));
        Assert.Contains("retention summary: runs removed=2 window=3d; taken removed=1 window=14d; results removed=1 window=14d; failed=2", lines);
    }

    [Xunit.Fact]
    public async Task Retention_DefaultWindows_SummaryStatesWindowsAndCounts()
    {
        SeedBareRepository();
        var run = AgedFolder("runs/111111111", 60);
        var job = AgedFile("queue/taken/old.json", 60);
        var cancel = AgedFile("queue/taken/old.cancel", 60);
        var resultFolder = AgedFolder("results/111111111/lane", 60);

        var result = await RunRetention();

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.False(Directory.Exists(run));
        Assert.False(File.Exists(job));
        Assert.False(File.Exists(cancel));
        Assert.False(Directory.Exists(resultFolder));
        Assert.Equal("retention summary: runs removed=1 window=3d; taken removed=2 window=14d; results removed=1 window=14d; failed=0",
            Assert.Single(OutputLines(result.Stdout).Where(line => line.StartsWith("retention summary:"))));
    }

    [Xunit.Fact]
    public async Task Retention_MalformedState_ReportsFailureWithoutDeletingEntries()
    {
        SeedBareRepository();
        var run = AgedFolder("runs/111111111", 60);
        var job = AgedFile("queue/taken/old.json", 60);
        var resultFolder = AgedFolder("results/111111111/lane", 60);
        File.WriteAllText(Path.Combine(_root, "runner-state.json"), "{broken");

        var result = await RunRetention();

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(run));
        Assert.True(File.Exists(job));
        Assert.True(Directory.Exists(resultFolder));
        Assert.Contains("retention skipped: runner state unreadable:", result.Stdout);
        Assert.Contains("retention summary: runs removed=0 window=3d; taken removed=0 window=14d; results removed=0 window=14d; failed=1",
            OutputLines(result.Stdout));
    }

    [Xunit.Fact]
    public async Task Retention_TakenMarkerAges_ExpireIndependentlyIncludingOrphans()
    {
        SeedBareRepository();
        var oldJob = AgedFile("queue/taken/old-job.json", 60);
        var newMarker = AgedFile("queue/taken/old-job.cancel", 0.5);
        var newJob = AgedFile("queue/taken/new-job.json", 0.5);
        var oldMarker = AgedFile("queue/taken/new-job.cancel", 60);
        var orphan = AgedFile("queue/taken/orphan.cancel", 60);

        var result = await RunRetention();

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.False(File.Exists(oldJob));
        Assert.True(File.Exists(newMarker));
        Assert.True(File.Exists(newJob));
        Assert.False(File.Exists(oldMarker));
        Assert.False(File.Exists(orphan));
    }

    [Xunit.Theory]
    [Xunit.InlineData("-RunFolderDays")]
    [Xunit.InlineData("-TakenDays")]
    [Xunit.InlineData("-ResultDays")]
    public async Task Retention_NegativeWindow_FailsBeforeDeletingAnything(string parameter)
    {
        var run = AgedFolder("runs/111111111", 60);
        var result = await RunRetention(parameter, "-1");
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(run));
        Assert.DoesNotContain("retention removed ", result.Stdout);
    }

    [Xunit.Fact]
    public async Task Retention_ZeroWindows_RemovesOldEntriesAndKeepsCancelledLog()
    {
        SeedBareRepository();
        var run = AgedFolder("runs/111111111", 0.5);
        var job = AgedFile("queue/taken/old.json", 0.5);
        var resultFolder = AgedFolder("results/111111111/lane", 0.5);
        var log = AgedFile("queue/taken/cancelled.log", 60);

        var result = await RunRetention("-RunFolderDays", "0", "-TakenDays", "0", "-ResultDays", "0");

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.False(Directory.Exists(run));
        Assert.False(File.Exists(job));
        Assert.False(Directory.Exists(resultFolder));
        Assert.Equal("fixture payload", File.ReadAllText(log));
    }

    private string AgedFolder(string relativePath, double days)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "payload.txt"), "fixture payload");
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        return path;
    }

    private string AgedFile(string relativePath, double days)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture payload");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        return path;
    }

    private void SeedBareRepository()
    {
        var path = Path.Combine(_root, "repo", "repo.git");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = RunGitProbe(_root, ["init", "--bare", "--quiet", path]);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.StandardError);
        Assert.True(File.Exists(Path.Combine(path, "HEAD")));
    }

    private static string[] OutputLines(string stdout) =>
        stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunRetention(params string[] windows) =>
        RunScript("Invoke-QueuedLanes.ps1", ["-RunRoot", _root, "-RetentionOnly", .. windows]);

    private async Task<(int ExitCode, string Stderr)> Run(string tools, string output)
    {
        var result = await RunScript("Get-ExecutorToolsDigest.ps1", ["-Path", tools, "-OutFile", output]);
        return (result.ExitCode, result.Stderr);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunScript(string scriptName, string[] arguments)
    {
        var script = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "executor", "tools", scriptName);
        Assert.True(File.Exists(script), "Tracked executor script is required for the process contract.");
        var start = new ProcessStartInfo(TestPowerShell.Executable)
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string[] allow = ["COMSPEC", "LOCALAPPDATA", "PATH", "PATHEXT", "PROGRAMDATA", "PROGRAMFILES",
            "PROGRAMFILES(X86)", "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "TMP", "WINDIR"];
        var required = start.Environment.Where(item => allow.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        start.Environment.Clear();
        foreach (var item in required) start.Environment[item.Key] = item.Value;
        start.Environment["USERPROFILE"] = _root;
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", script }.Concat(arguments))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Missing event: executor script process exit: " + scriptName);
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
