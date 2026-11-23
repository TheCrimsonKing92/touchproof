using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class FixtureGitReplay
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string Runner = "scripts/Invoke-TestSummary.ps1";
    private const string CensusRevision = "8c104aa81cc389c43d8a9d1bd74d19121048b55e";
    private const string FixtureRevision = "08f295076eae4d1c14660a846e3c917de32d7a9d";
    private const string ExclusionTip = "2464c323f6901cecac8889547631a2076817dcf1";
    private const string WorktreeClass = "GoalWorktreeTestsRebaseMergeNoHistoryRewrite";
    private const string FactoryClass = "WorkerDispatchTestsSeededRepositoryFactoryTests";
    private const string FactoryMethod = "Create_MultipleCopies_ProducesIndependentCommittedRepositories";

    internal sealed record ReplayHost(
        Func<string, string, string[], string> RunNative,
        Func<string> LocalAppData,
        Action<string> LabelStaging,
        Func<ProcessStartInfo, string, string, FixtureGitReplayTrace.Job> Capture,
        Func<string, FixtureGitReplayTrace.Job, List<FixtureGitReplayTrace.Start>> ReadTree);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    internal static int Run(string[] args, ReplayHost? host = null)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.Error.WriteLine("Usage: dotnet Mcg.AgentOrchestrator.ConsoleIoProbe.dll --fixture-git-replay <repo> <new-evidence-directory> [configuration]");
            return 64;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Fixture Git replay requires an ETW-capable Windows host.");
            host ??= new ReplayHost(RunNative, ResolveLocalAppData, LabelStaging,
                FixtureGitReplayTrace.Capture, FixtureGitReplayTrace.ReadTree);
            var receipt = Replay(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]),
                args.Length == 3 ? args[2] : "Debug", host);
            Console.WriteLine($"Fixture Git replay receipt {receipt}");
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string Replay(string repo, string output, string configuration, ReplayHost host)
    {
        var revision = host.RunNative(repo, "git.exe", ["rev-parse", "HEAD"]);
        if (host.RunNative(repo, "git.exe", ["status", "--porcelain"]).Length != 0)
            throw new InvalidOperationException("Start replay from a clean candidate checkout.");
        foreach (var ancestor in new[] { FixtureRevision, ExclusionTip, CensusRevision })
            host.RunNative(repo, "git.exe", ["merge-base", "--is-ancestor", ancestor, revision]);
        var parents = host.RunNative(repo, "git.exe", ["log", "-1", "--format=%P"]);
        var origin = host.RunNative(repo, "git.exe", ["remote", "get-url", "origin"]);
        var gitVersion = host.RunNative(repo, "git.exe", ["--version"]);
        var sdk = host.RunNative(repo, "dotnet.exe", ["--version"]);
        var powerShell = host.RunNative(repo, WorkerShell.Executable, ["-NoProfile", "-NonInteractive", "--version"]);
        var launcherHash = Hash(typeof(FixtureGitReplay).Assembly.Location);
        var runnerHash = Hash(Path.Combine(repo, Runner));

        if (Path.Exists(output))
            throw new IOException("Use a new fixture Git evidence directory for each arm.");
        Directory.CreateDirectory(output);

        var selections = new[]
        {
            CaptureSelection(repo, output, configuration, "goal-worktree", WorktreeClass,
                $"FullyQualifiedName={WorktreeClass}",
                [
                    "MergeIntegratedHistory_PreservesEarlierGoalCommitsAndExactTree",
                    "ExistingSquashPath_NegativeControlDropsReviewedHeadFromAncestry",
                    "OverlappingConflict_AbortsAndLeavesBranchAndCheckoutUnchanged",
                    "AlreadyContainsMain_LeavesBranchUnchanged",
                    "DirtyWorktree_PreservesBranchAndUncommittedContent",
                    "AdditiveConflict_MergesWithOriginalHeadAsFirstParent",
                    "MissingBranch_ReturnsRecoveryWithoutCreatingBranch",
                    "MissingWorktree_LeavesExistingBranchUnchanged"
                ], host),
            CaptureSelection(repo, output, configuration, "seeded-factory", FactoryClass,
                $"FullyQualifiedName={FactoryClass}&Name~{FactoryMethod}", [FactoryMethod], host)
        };

        var artifacts = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => new
            {
                path = Path.GetRelativePath(output, path), bytes = new FileInfo(path).Length, sha256 = Hash(path)
            }).ToArray();
        var receipt = Path.Combine(output, "replay.json");
        var cpu = selections.Sum(selection => selection.Cpu100ns);
        WriteJson(receipt, new
        {
            schema = 2, revision, repository = repo, origin, candidate_parents = parents,
            measurement_runner_sha256 = launcherHash, test_summary_sha256 = runnerHash,
            census_revision = CensusRevision, historical_probe_entries = 189105, historical_verifier_attempts = 33,
            pinned_fixture_revision = FixtureRevision, shipped_exclusion_tip = ExclusionTip,
            shipped_exclusion_goal = "69d0be49", configuration, os = Environment.OSVersion.VersionString,
            git = gitVersion, dotnet_sdk = sdk, powershell = powerShell,
            boundary = "suspended launcher creation through all contained processes exiting",
            cpu_100ns = cpu, cpu_seconds = TimeSpan.FromTicks(cpu).TotalSeconds,
            native_git = selections.Sum(selection => selection.NativeGit), selections, artifacts
        });
        return receipt;
    }

    [SupportedOSPlatform("windows")]
    private static FixtureGitReplayEvidence.Selection CaptureSelection(string repo, string output, string configuration,
        string family, string testClass, string filter, string[] methods, ReplayHost host)
    {
        var directory = Directory.CreateDirectory(Path.Combine(output, family)).FullName;
        var lowRoot = TempRootJanitor.ResolveLowIntegrityOwnedRootParent(Path.GetFullPath(host.LocalAppData()));
        var staging = Directory.CreateDirectory(Path.Combine(lowRoot, $"fixture-git-{Guid.NewGuid():N}")).FullName;
        var ownership = Path.Combine(staging, ".mtp-run-ownership.json");
        FixtureGitReplayTrace.Job job;
        var trace = Path.Combine(directory, "process.etl");
        try
        {
            using var self = Process.GetCurrentProcess();
            WriteJson(ownership, new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                ["attemptId"] = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_GATE_ATTEMPT_ID") ?? "unowned",
                ["machineName"] = Environment.MachineName,
                ["ownerProcessId"] = self.Id,
                ["ownerProcessStartedAt"] = self.StartTime.ToUniversalTime().ToString("O"),
                ["createdAt"] = DateTimeOffset.UtcNow,
                ["runLabel"] = "fixture-git"
            });
            host.LabelStaging(staging);
            var runner = StartInfo(repo, WorkerShell.Executable, "-NoProfile", "-NonInteractive", "-File", Path.Combine(repo, Runner),
                "-Target", Path.Combine(repo, Project), "-Configuration", configuration, "-NoBuild", "-Filter", filter,
                "-ResultsRoot", Path.Combine(staging, "tests"), "-SlotTimingFile", Path.Combine(staging, "slot.jsonl"),
                "-TestInput", $"MCG_TEST_INPUT_FIXTURE_GIT_PROBE_TRACE={Path.Combine(staging, "probes.jsonl")}");
            job = host.Capture(runner, directory, trace);
        }
        finally
        {
            // Retain raw artifacts before deleting the owned staging directory, even on capture failure.
            foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                if (source == ownership) continue;
                var target = Path.Combine(directory, Path.GetRelativePath(staging, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            Directory.Delete(staging, recursive: true);
        }

        var tree = host.ReadTree(trace, job);
        return FixtureGitReplayEvidence.Reconcile(directory, family, testClass, filter, methods, tree, job);
    }

    private static string ResolveLocalAppData()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrWhiteSpace(localAppData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppData;
    }

    [SupportedOSPlatform("windows")]
    private static void LabelStaging(string staging)
    {
        var label = NativeIntegrityLabelWriter.Write(staging, "(OI)(CI)L", recursive: false);
        if (!label.Succeeded)
            throw new Win32Exception(label.Win32Error, $"Could not label fixture replay staging directory {staging} ({label.Reason}).");
    }

    internal static ProcessStartInfo StartInfo(string directory, string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }

    internal static string RunNative(string directory, string executable, params string[] args)
    {
        var start = StartInfo(directory, executable, args);
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} {string.Join(' ', args)} exited {process.ExitCode}. {stdout.Result} {stderr.Result}".TrimEnd());
        return stdout.Result.Trim();
    }

    internal static void WriteJson<T>(string path, T value)
    {
        using var file = File.Open(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(file, value, JsonOptions);
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
