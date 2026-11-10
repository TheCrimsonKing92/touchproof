using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The contained child encloses source selection, discovery, tests and owned setup/cleanup.
// The parent is the only writer of measured resource fields.
internal static class ExperimentReplayProcedure
{
    internal const string Command = "__experiment-replay-procedure";
    internal sealed record Request(string Checkout, string BaseCommit, string CandidateCommit, string Arm,
        string Dotnet, string BuildArtifacts, string ResultsDirectory, string CompletionPath, bool Reference = false,
        IReadOnlyList<ExperimentReplaySource>? Patches = null);
    internal sealed record Completion(string State, string? Reason, ExperimentReplaySelection? Selection,
        IReadOnlyList<string> FailingTests, IReadOnlyList<ExperimentReplaySource> Sources);

    internal static async Task<int> RunAsync(string requestPath, CancellationToken token)
    {
        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), ExperimentReplayStore.Json)
            ?? throw new InvalidDataException("Missing replay procedure request.");
        Completion completion;
        try
        {
            try { completion = await ExecuteAsync(request, token); }
            finally
            {
                // Only the verified detached checkout is reset. Build/results/runtime stay outside it.
                var head = GitCli.RunForRepository(request.Checkout, "rev-parse", "HEAD");
                if (File.Exists(Path.Combine(request.Checkout, ".git")) && head.Succeeded && head.Output.Trim() == request.CandidateCommit)
                {
                    Git(request.Checkout, "reset", "--hard", "--quiet", request.CandidateCommit);
                    Git(request.Checkout, "clean", "-fdx", "--quiet");
                }
                else throw new InvalidDataException("Procedure cleanup lost its pinned detached-worktree identity.");
            }
        }
        catch (Exception ex) { completion = new("apparatus-failed", ex.Message, null, [], []); }
        Directory.CreateDirectory(Path.GetDirectoryName(request.CompletionPath)!);
        var temporary = request.CompletionPath + ".publishing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(completion, ExperimentReplayStore.Json));
        File.Move(temporary, request.CompletionPath, overwrite: false);
        return completion.State == "apparatus-failed" ? 1 : 0;
    }

    internal static async Task<Completion> ExecuteAsync(Request request, CancellationToken token)
    {
        Directory.CreateDirectory(request.ResultsDirectory);
        var head = GitCli.RunForRepository(request.Checkout, "rev-parse", "HEAD");
        if (!head.Succeeded || head.Output.Trim() != request.CandidateCommit || !File.Exists(Path.Combine(request.Checkout, ".git")))
            throw new InvalidDataException("Procedure requires its owned detached worktree at the pinned candidate.");
        Git(request.Checkout, "reset", "--hard", "--quiet", request.CandidateCommit);
        Git(request.Checkout, "clean", "-fdx", "--quiet");
        ApplyPatches(request.Checkout, request.Patches ?? []);
        var selected = ExperimentReplaySelectionReader.Read(request.Checkout, request.BaseCommit, request.CandidateCommit,
            request.Arm, request.Patches is { Count: > 0 });
        var settings = AcceptanceGateEngineSettings.Load(request.Checkout);
        var environment = new DotnetBuildEnvironment("experiment-replay", request.BuildArtifacts, request.BuildArtifacts,
            Path.Combine(request.BuildArtifacts, "execution.lock"), [], "experiment-replay");
        var failures = new SortedSet<string>(StringComparer.Ordinal);
        var evidence = new List<ExperimentReplaySource>();
        var ran = 0;
        var routing = new ImpactReplayChangedSourceRouting(request.Checkout);
        foreach (var project in selected.Projects)
        {
            var sources = routing.TestProjects.Single(p => Path.GetRelativePath(request.Checkout, p.Path).Replace('\\', '/') == project).Sources;
            var available = AcceptanceTestClassSourceScanner.Scan(request.Checkout, sources.Where(File.Exists).ToArray());
            var classes = available.Where(c => request.Reference || selected.Classes.Contains(c.FullName, StringComparer.Ordinal))
                .Select(c => c.FullName).ToArray();
            if (classes.Length == 0) continue;
            if (DotnetTestCheckTranslator.ResolveRunner(request.Checkout, project) != "mtp")
                throw new InvalidDataException($"Unsupported replay runner for {project}.");
            var results = Path.Combine(request.ResultsDirectory, ExperimentReplayStore.Hash(project)[..12]);
            Directory.CreateDirectory(results);
            var trx = Path.Combine(results, "tests.trx");
            var arguments = classes.SelectMany(c => new[] { "--filter-class", c }).ToArray();
            var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
            { Name = "experiment-replay", Type = "dotnet-test", Project = project, Arguments = arguments, Runner = "mtp" };
            var command = AcceptanceCheckCommandBuilder.BuildMtpTestArguments(check, settings, environment,
                new([trx], []), excludeHostIntegration: true,
                (_, filter) => AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter));
            command[0] = request.Dotnet;
            var result = await AcceptanceCheckProcessInvoker.RunAsync(command, request.Checkout, TimeSpan.FromMinutes(10),
                false, token, default, null, new AcceptanceProcessRequest { ResolveBareCommand = true });
            if (result.StdoutPath is { } stdout && File.Exists(stdout)) evidence.Add(ExperimentReplayStore.Source(stdout));
            if (result.StderrPath is { } stderr && File.Exists(stderr)) evidence.Add(ExperimentReplayStore.Source(stderr));
            if (result.TimedOut || !File.Exists(trx))
                return new("apparatus-failed", "Test timeout or required TRX missing.", selected, failures.ToArray(), evidence);
            var tests = ExperimentReplayEvidence.Tests(trx);
            var executed = tests.Where(t => t.Outcome is "Passed" or "Failed").ToArray();
            if (executed.Length == 0) return new("apparatus-failed", "Zero executed tests in selected project.", selected, [], evidence);
            ran += executed.Length;
            failures.UnionWith(executed.Where(t => t.Outcome == "Failed").Select(t => t.Identity));
            evidence.Add(ExperimentReplayStore.Source(trx));
            if (result.ExitCode != 0 && !executed.Any(t => t.Outcome == "Failed"))
                return new("apparatus-failed", $"Test host exit {result.ExitCode} without failed-test evidence.", selected, failures.ToArray(), evidence);
        }
        return ran == 0 ? new("apparatus-failed", "No projects executed.", selected, [], evidence)
            : new(failures.Count > 0 ? "test-failed" : "passed", null, selected, failures.ToArray(), evidence);
    }

    internal static void ApplyPatches(string checkout, IReadOnlyList<ExperimentReplaySource> patches)
    {
        foreach (var patch in patches)
        {
            if (ExperimentReplayStore.Source(patch.Identity).Sha256 != patch.Sha256) throw new InvalidDataException("Mutation patch identity mismatch.");
            Git(checkout, "apply", "--check", "--", patch.Identity);
            Git(checkout, "apply", "--", patch.Identity);
        }
    }

    private static void Git(string checkout, params string[] args)
    {
        var result = GitCli.RunForRepository(checkout, args);
        if (!result.ProcessStarted || !result.Succeeded || result.DrainTimedOut) throw new InvalidDataException($"Procedure reset/mutation failed: {result.Error}");
    }
}
