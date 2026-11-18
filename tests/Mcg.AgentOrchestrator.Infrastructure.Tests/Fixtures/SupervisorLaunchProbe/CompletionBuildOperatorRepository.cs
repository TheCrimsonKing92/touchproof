using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Disposable fixture data only. No operator store or checkout is opened by this probe.
internal sealed record CompletionBuildOperatorRepository(
    string Root, string Source, string GateToken, string Goal, string Task, string DisjointGoal, string DisjointTask,
    string InitialHead)
{
    internal string State => Path.Combine(Root, "state.db");
    internal string Repository => Path.Combine(Root, "repository");
    internal string Storage => Path.Combine(Root, "build-storage");
    internal string Stop => Path.Combine(Root, "conductor.stop");
    internal string Receipts => Path.Combine(Root, "receipts.jsonl");
    internal GoalId GoalId => new(Goal);
    internal TaskId TaskId => new(Task);
    internal static CompletionBuildOperatorRepository Load(string root) =>
        JsonSerializer.Deserialize<CompletionBuildOperatorRepository>(File.ReadAllText(Path.Combine(root, "scenario.json")))!;

    internal static CompletionBuildOperatorRepository Create(string root, string source, string token)
    {
        Require(Path.IsPathFullyQualified(root) && !Directory.Exists(root), "Scenario root must be a new absolute directory.");
        Require(File.Exists(Path.Combine(source, "Mcg.AgentOrchestrator.sln")), "Explicit source repository is required.");
        Directory.CreateDirectory(root);
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(repository);
        Git(repository, "init", "-b", "main");
        Git(repository, "config", "user.name", "Completion build operator fixture");
        Git(repository, "config", "user.email", "completion-build-fixture@example.invalid");
        File.WriteAllText(Path.Combine(repository, "seed.txt"), "Disposable completion-build operator scenario\n");
        Git(repository, "add", "seed.txt");
        Git(repository, "commit", "-m", "Seed isolated operator scenario");
        var head = Git(repository, "rev-parse", "HEAD");
        var kernel = new AgentOrchestratorKernel();
        var agent = new AgentDefinition(new AgentId("fixture-developer"), "Fixture developer", AgentRole.Developer,
            new ModelProfile("OpenAI", "fixture-never-dispatched", ModelCapability.Text, SubscriptionMode.ApiKey));
        var task = new TaskSpec(TaskId.New(), "Build the isolated Feature project", AgentRole.Developer);
        var goal = kernel.CreateGoal("Held managed completion build", [task]);
        kernel.ActivateGoal(goal.Id, [agent]);
        var otherTask = new TaskSpec(TaskId.New(), "Complete a disjoint documentation change", AgentRole.Developer);
        var other = kernel.CreateGoal("Disjoint progress while compilation is held", [otherTask]);
        kernel.ActivateGoal(other.Id, [agent]);
        var f = new CompletionBuildOperatorRepository(root, source, token, goal.Id.Value, task.Id.Value,
            other.Id.Value, otherTask.Id.Value, head);
        var worktree = GoalWorktrees.Ensure(repository, goal.Id);
        Directory.CreateDirectory(Path.Combine(worktree, "src", "Feature"));
        // The checkpoint is outside the candidate and build-storage trees. Its wait is an event,
        // not a delay; only the independent controller has the release handle.
        var command = $"&quot;{SecurityElement.Escape(Environment.ProcessPath!)}&quot; &quot;{SecurityElement.Escape(typeof(CompletionBuildOperatorRepository).Assembly.Location)}&quot; completion-build-gate &quot;{SecurityElement.Escape(root)}&quot;";
        File.WriteAllText(Path.Combine(worktree, "src", "Feature", "Feature.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <UseSharedCompilation>false</UseSharedCompilation>
                <RestoreConfigFile>$(MSBuildThisFileDirectory)../../NuGet.Config</RestoreConfigFile>
              </PropertyGroup>
              <Target Name="OperatorHeldBuild" BeforeTargets="CoreCompile">
                <Exec Command="{{command}}" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(worktree, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(worktree, "src", "Feature", "Feature.cs"), "public sealed class Feature { }\n");
        SeedDispatch(kernel, goal, task, worktree, head, "src/Feature/Feature.cs");
        WorkerBuildProjectScope.Prepare(worktree, goal.Id.Value, task.Id.Value, "main");
        CaptureScope(kernel, goal.Id, task, worktree, head);
        var otherTree = GoalWorktrees.Ensure(repository, other.Id);
        File.WriteAllText(Path.Combine(otherTree, "disjoint.md"), "Disjoint candidate\n");
        SeedDispatch(kernel, other, otherTask, otherTree, head, "disjoint.md");
        _ = StateDbMigrations.EnsureUpToDate(f.State);
        new SqliteOrchestratorStateRepository(f.State).SaveAsync(kernel).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(f));
        return f;
    }

    internal static void SeedDispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        string worktree, string head, string changed)
    {
        var logs = Path.Combine(Path.GetDirectoryName(worktree)!, "fixture-logs", goal.Id.Value);
        Directory.CreateDirectory(logs);
        var stdout = Path.Combine(logs, "worker.out.log");
        var stderr = Path.Combine(logs, "worker.err.log");
        var exit = Path.Combine(logs, "worker.exit.txt");
        File.WriteAllText(stdout, $"""
            WORKER_RESULT:
            files: {changed}
            commands: prepared isolated candidate
            tests: deferred - operator scenario
            commit: {head}
            blockers: none
            assigned_scope_complete: true
            model_fit: fixture - adequate - seeded exited worker
            skills: none
            confidence: high
            END_WORKER_RESULT
            """);
        File.WriteAllText(stderr, "");
        // A real, already exited process identity represents the prepared worker. No model runs.
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("exit");
        info.ArgumentList.Add("0");
        using var process = Process.Start(info)!;
        var at = new DateTimeOffset(process.StartTime.ToUniversalTime());
        process.WaitForExit();
        File.WriteAllText(exit, process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new("fixture", "prepared exited worker", worktree, at));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new(process.Id, "prepared exited worker", worktree, stdout, stderr, exit, at, null, null));
    }

    internal AgentOrchestratorKernel LoadKernel() => new SqliteOrchestratorStateRepository(State)
        .LoadGoalsAsync([GoalId, new GoalId(DisjointGoal)]).GetAwaiter().GetResult();
    internal void Save(AgentOrchestratorKernel kernel) => new SqliteOrchestratorStateRepository(State)
        .SaveAsync(kernel).GetAwaiter().GetResult();
    internal void Record(string kind, object payload)
    {
        using var mutex = new Mutex(false, GateToken + "-receipts");
        Require(mutex.WaitOne(TimeSpan.FromSeconds(30)), "Receipt writer did not release its mutex.");
        try { File.AppendAllText(Receipts, JsonSerializer.Serialize(new
            { kind, at = DateTimeOffset.UtcNow, pid = Environment.ProcessId, payload }) + Environment.NewLine); }
        finally { mutex.ReleaseMutex(); }
    }
    internal static string Git(string repository, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repository, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(repository);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        process.WaitForExitAsync(guard.Token).GetAwaiter().GetResult();
        Require(process.ExitCode == 0, stderr.GetAwaiter().GetResult());
        return stdout.GetAwaiter().GetResult().Trim();
    }
    internal static void CaptureScope(AgentOrchestratorKernel kernel, GoalId goal, TaskSpec task, string worktree, string head) =>
        kernel.RecordDispatchBaseCommit(goal, task.Id, head,
            WorkerBuildProjectScope.ReadDispatchIdentity(worktree, head) with { CapturedDispatchAt = task.LastDispatch!.DispatchedAt });
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
