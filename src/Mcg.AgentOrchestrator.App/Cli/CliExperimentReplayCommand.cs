using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliExperimentReplayCommand
{
    internal const string Usage = "Usage: experiment-replay import [--json] | register --value-bound <receipt.json> [--seed <int>] [--json] | run --registration <id> [--json] | report --run <id> [--json] | fault --run <id> --spec <faults.json> [--json]";
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 && args[0] == "experiment-replay";
    internal static bool IsQuery(IReadOnlyList<string> args) => IsCommand(args) && args.Count > 1 && args[1] == "report";
    internal sealed record ValueBound(string Project, string CorpusIdentity, double OptimisticAvoidableTestCpuMilliseconds,
        IReadOnlyList<ExperimentReplaySource> SourceReceipts, string Analysis);

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace, CancellationToken token = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return await RunAsync(args, workspace, Console.Out, Console.Error, cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
    }

    // Routing has no provider/catalog/worker dependency. Tests exercise the same operator entry point.
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        TextWriter output, TextWriter error, CancellationToken token = default)
    {
        if (args.Count == 2 && args[1] is "--help" or "-h") { output.WriteLine(Usage); return 0; }
        if (args.Count < 2 || args[1] is not ("import" or "register" or "run" or "report" or "fault")) return Invalid();
        var action = args[1];
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--json" && !json) { json = true; continue; }
            if (args[i] is not ("--registration" or "--run" or "--seed" or "--value-bound" or "--spec") ||
                i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !flags.TryAdd(args[i], args[i + 1])) return Invalid();
            i++;
        }
        var allowed = action switch { "register" => new[] { "--seed", "--value-bound" }, "run" => ["--registration"], "report" => ["--run"], "fault" => ["--run", "--spec"], _ => [] };
        if (flags.Keys.Any(f => !allowed.Contains(f, StringComparer.Ordinal))) return Invalid();
        if (action == "register" && !flags.ContainsKey("--value-bound") || action == "run" && !flags.ContainsKey("--registration") ||
            action == "report" && !flags.ContainsKey("--run") || action == "fault" && (!flags.ContainsKey("--run") || !flags.ContainsKey("--spec"))) return Invalid();
        var seed = 20261109;
        if (flags.TryGetValue("--seed", out var seedText) && !int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed)) return Invalid();
        try
        {
            // Home-project only; project/state binding comes from the existing explicit CLI context.
            if (!File.Exists(Path.Combine(workspace.ExecutionDirectory, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj")))
                throw new InvalidDataException("The initial pilot requires the home orchestrator repository.");
            var database = Path.Combine(workspace.OrchestratorDirectory, ExperimentReplayStore.DatabaseName);
            if (action is "import" or "register") ExperimentReplayStore.Setup(database);
            var store = new ExperimentReplayStore(database, readOnly: action == "report");
            var root = Path.Combine(workspace.OrchestratorDirectory, "experiment-replay");
            if (action == "import")
            {
                var inventory = await ExperimentReplayCorpusImporter.ImportAsync(workspace, store, DateTimeOffset.UtcNow, token);
                output.WriteLine(json ? JsonSerializer.Serialize(inventory, ExperimentReplayStore.Json)
                    : $"Corpus: {inventory.Considered} rounds; {inventory.Added} new; {inventory.Replayable} replayable; excluded/unknown={inventory.Considered - inventory.Replayable}.");
                return 0;
            }
            if (action == "register")
            {
                var now = DateTimeOffset.UtcNow;
                var inventory = await ExperimentReplayCorpusImporter.ImportAsync(workspace, store, now, token);
                var bound = JsonSerializer.Deserialize<ValueBound>(File.ReadAllText(flags["--value-bound"]), ExperimentReplayStore.Json)
                    ?? throw new InvalidDataException("Missing operator value-bound receipt.");
                ValidateBound(bound, workspace.ProjectName, inventory.Cases);
                var cases = inventory.Cases.Where(c => c.Replayability == "replayable").Take(20).ToArray();
                if (cases.Length < 20)
                {
                    var stop = JsonSerializer.Serialize(new { state = "insufficient-corpus", replayable = cases.Length, required = 20,
                        decision = "Operator decision required before substituting history or starting expensive builds." }, ExperimentReplayStore.Json);
                    ExperimentReplayEvidence.Write(Path.Combine(root, "stops"), stop, ".json");
                    output.WriteLine(stop);
                    return 0;
                }
                var assembly = typeof(CliExperimentReplayCommand).Assembly.Location;
                var originalRuntime = Path.GetDirectoryName(assembly)!;
                var runtimeIdentity = ExperimentReplayRunner.RuntimeIdentity(originalRuntime);
                var runtime = Path.Combine(root, "runtimes", runtimeIdentity);
                CopyRuntime(originalRuntime, runtime);
                var dotnet = HandleListProcessLauncher.ResolveApplication("dotnet");
                var info = await ExperimentReplayProcess.RunAsync(dotnet, ["--version"], workspace.ExecutionDirectory,
                    Path.Combine(root, "toolchain", Guid.NewGuid().ToString("N")), TimeSpan.FromMinutes(1), token);
                if (info.ExitCode != 0 || info.State != "exited") throw new InvalidDataException("Toolchain identity unavailable.");
                var toolchain = ExperimentReplayStore.Hash(ExperimentReplayStore.Source(dotnet).Sha256 + File.ReadAllText(info.StdoutPath).Trim());
                var configuration = ConfigurationIdentity(workspace.ExecutionDirectory);
                var boundSource = ExperimentReplayEvidence.Copy(Path.Combine(root, "evidence"), flags["--value-bound"]);
                var registration = new ExperimentReplayRegistration(Guid.NewGuid().ToString("N"), workspace.ProjectName, now,
                    seed, runtimeIdentity, toolchain, configuration, cases, boundSource);
                store.Register(registration);
                ExperimentReplayEvidence.Write(Path.Combine(root, registration.Id),
                    JsonSerializer.Serialize(ExperimentReplaySchedule.Create(registration), ExperimentReplayStore.Json), ".schedule.json");
                output.WriteLine(json ? JsonSerializer.Serialize(registration, ExperimentReplayStore.Json) : $"Registered: {registration.Id}; 20 cases; fixed A/A then A/B.");
                return 0;
            }
            if (action == "run")
            {
                var registration = store.Registration(flags["--registration"]);
                if (registration.Project != workspace.ProjectName || registration.ConfigurationIdentity != ConfigurationIdentity(workspace.ExecutionDirectory))
                    throw new InvalidDataException("Project/configuration identity changed since registration.");
                if (ExperimentReplayStore.Source(registration.ValueBoundEvidence.Identity).Sha256 != registration.ValueBoundEvidence.Sha256)
                    throw new InvalidDataException("Value-bound evidence changed.");
                var dotnet = HandleListProcessLauncher.ResolveApplication("dotnet");
                var info = await ExperimentReplayProcess.RunAsync(dotnet, ["--version"], workspace.ExecutionDirectory,
                    Path.Combine(root, "toolchain", Guid.NewGuid().ToString("N")), TimeSpan.FromMinutes(1), token);
                if (info.ExitCode != 0 || registration.ToolchainIdentity != ExperimentReplayStore.Hash(
                    ExperimentReplayStore.Source(dotnet).Sha256 + File.ReadAllText(info.StdoutPath).Trim()))
                    throw new InvalidDataException("Registered toolchain changed.");
                var assembly = Path.Combine(root, "runtimes", registration.RuntimeIdentity, Path.GetFileName(typeof(CliExperimentReplayCommand).Assembly.Location));
                var run = await ExperimentReplayRunner.RunAsync(store, registration, root,
                    new ExperimentReplayRunner.LocalHost(workspace.ExecutionDirectory, dotnet, assembly, toolchainIdentity: registration.ToolchainIdentity), token);
                ExperimentReplayReportWriter.Write(output, ExperimentReplayReportWriter.Create(registration, run, store.Executions(run.Id), store.Faults(run.Id)), json);
                return run.State == "apparatus-failed" ? 1 : 0;
            }
            var recorded = store.Run(flags["--run"]);
            var registered = store.Registration(recorded.RegistrationId);
            if (registered.Project != workspace.ProjectName) throw new InvalidDataException("Report belongs to a different project.");
            if (action == "fault")
            {
                var definitions = JsonSerializer.Deserialize<ExperimentReplayFaultRunner.Definition[]>(File.ReadAllText(flags["--spec"]), ExperimentReplayStore.Json)
                    ?? throw new InvalidDataException("Missing fault definitions.");
                var assembly = Path.Combine(root, "runtimes", registered.RuntimeIdentity, Path.GetFileName(typeof(CliExperimentReplayCommand).Assembly.Location));
                var outcomes = await ExperimentReplayFaultRunner.RunAsync(store, registered, recorded, definitions,
                    root, workspace.ExecutionDirectory, HandleListProcessLauncher.ResolveApplication("dotnet"), assembly, token);
                output.WriteLine(JsonSerializer.Serialize(outcomes, ExperimentReplayStore.Json));
                return outcomes.Any(o => o.State == "apparatus-failed") ? 1 : 0;
            }
            ExperimentReplayReportWriter.Write(output, ExperimentReplayReportWriter.Create(registered, recorded, store.Executions(recorded.Id), store.Faults(recorded.Id)), json);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (action == "register")
                ExperimentReplayEvidence.Write(Path.Combine(workspace.OrchestratorDirectory, "experiment-replay", "stops"),
                    JsonSerializer.Serialize(new { state = "registration-stopped", reason = ex.Message }, ExperimentReplayStore.Json), ".json");
            error.WriteLine($"Experiment replay failed: {ex.Message}"); return 1;
        }
        int Invalid() { error.WriteLine(Usage); return 2; }
    }

    internal static string CorpusIdentity(IReadOnlyList<ExperimentReplayCase> cases) =>
        ExperimentReplayStore.Hash(JsonSerializer.Serialize(cases, ExperimentReplayStore.Json));

    internal static void ValidateBound(ValueBound bound, string project, IReadOnlyList<ExperimentReplayCase> cases)
    {
        if (bound.Project != project || bound.CorpusIdentity != CorpusIdentity(cases) ||
            !double.IsFinite(bound.OptimisticAvoidableTestCpuMilliseconds) || bound.OptimisticAvoidableTestCpuMilliseconds <= 0 ||
            bound.SourceReceipts is not { Count: > 0 } || string.IsNullOrWhiteSpace(bound.Analysis))
            throw new InvalidDataException("Zero/unknown/unbound value bound: operator review required before expensive replay builds.");
        foreach (var source in bound.SourceReceipts)
            if (ExperimentReplayStore.Source(source.Identity).Sha256 != source.Sha256)
                throw new InvalidDataException("Value-bound source receipt hash mismatch.");
    }

    private static string ConfigurationIdentity(string checkout) => ExperimentReplayStore.Hash(string.Join("\n",
        new[] { "global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "config/acceptance-manifest.json" }
            .Select(p => Path.Combine(checkout, p)).Where(File.Exists).Select(p => Path.GetFileName(p) + ":" + ExperimentReplayStore.Source(p).Sha256)));

    private static void CopyRuntime(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(path, target);
        }
        if (ExperimentReplayRunner.RuntimeIdentity(source) != ExperimentReplayRunner.RuntimeIdentity(destination))
            throw new InvalidDataException("Isolated runtime identity mismatch.");
    }
}
