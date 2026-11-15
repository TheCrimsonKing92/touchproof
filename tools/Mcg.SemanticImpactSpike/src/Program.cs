using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Build.Locator;

namespace Mcg.SemanticImpactSpike;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        MSBuildLocator.RegisterDefaults();
        return await RunAsync(args);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string? receipt = null;
        var paths = new ReceiptPaths(Environment.CurrentDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        try
        {
            if (args.Length == 0 || args[0] is not ("verify" or "measure" or "query" or "redact"))
                throw new ArgumentException("Usage: verify|measure|query --repo ROOT --data FILE --receipt FILE [--msbuild-property KEY=VALUE] [--id QUERY]; redact --repo ROOT --receipt FILE");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Configuration"] = "Debug" };
            for (var i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("Missing option value: " + args[i]);
                if (args[i] == "--msbuild-property")
                {
                    var pair = args[i + 1].Split('=', 2);
                    if (pair.Length != 2) throw new ArgumentException("MSBuild property must be KEY=VALUE");
                    properties.Add(pair[0], pair[1]);
                }
                else if (args[i] is "--repo" or "--data" or "--receipt" or "--id") options.Add(args[i], args[i + 1]);
                else throw new ArgumentException("Unknown option: " + args[i]);
            }
            var root = Path.GetFullPath(options["--repo"]);
            paths = new ReceiptPaths(root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            receipt = Path.GetFullPath(options["--receipt"]);
            if (args[0] == "redact")
            {
                using var original = JsonDocument.Parse(File.ReadAllText(receipt));
                Write(receipt, paths, original.RootElement);
                Console.WriteLine("receipt redacted");
                return 0;
            }
            var dataPath = Path.GetFullPath(options["--data"]);
            var data = QueryData.Read(dataPath);
            if (!await RepositoryBaseline.MatchesAsync(root, data))
            {
                Write(receipt, paths, new { Status = "baseline drift", Current = await RepositoryBaseline.ReadAsync(root), data.SourceTree, data.TestsTree });
                Console.WriteLine("baseline drift; receipt=" + receipt);
                return 3;
            }
            var fixtureChecks = args[0] == "verify" ? (await SpikeChecks.FixtureAsync(root, properties)).Concat(SpikeChecks.ReceiptPaths()).ToArray() : [];
            // The measured boundary excludes fixture verification, CLI parsing and git identity checks.
            var timer = Stopwatch.StartNew();
            using var loader = new SolutionWorkspaceLoader(properties);
            await loader.OpenAsync(Path.Combine(root, "Mcg.AgentOrchestrator.sln"));
            var index = new ReverseReferenceIndex();
            if (!loader.Failed) index.Build(loader, root);
            var queries = options.TryGetValue("--id", out var id) ? data.Queries.Where(q => q.Id == id).ToArray() : data.Queries;
            if (queries.Length == 0) throw new ArgumentException("No matching query");
            var results = loader.Failed ? [] : SpikeChecks.KnownAnswers(index, data with { Queries = queries });
            var total = timer.Elapsed.TotalSeconds;
            // Variant is measured separately, never charged to the scored decision number.
            var variant = loader.Failed ? [] : data.Queries.Where(q => q.OperatorNumber is >= 8 and <= 10)
                .Select(q => new ImpactQueryEngine(index).Execute(q, frameworkDispatch: true)).ToArray();
            var peak = Process.GetCurrentProcess().PeakWorkingSet64;
            var failed = loader.Failed || fixtureChecks.Any(c => !c.Passed) || results.Any(r => r.Score is "wrong" or "partial");
            var requiredCorrect = data.Queries.Where(q => q.DecisionRequired)
                .All(q => results.Any(r => r.Id == q.Id && r.Score == "correct"));
            var cost = total <= 45 ? "small" : total <= 120 ? "conditional" : "over-limit";
            var verdict = !loader.Failed && requiredCorrect && total <= 45 && peak <= 4L * 1024 * 1024 * 1024 ? "proceed" : "stop";
            Write(receipt, paths, new
            {
                Status = loader.Failed ? "analysis failure" : failed ? "checks failed" : "passed",
                TimestampUtc = DateTimeOffset.UtcNow, Command = args, Baseline = data.BaselineRevision,
                data.SourceTree, data.TestsTree, DataHash = QueryData.Hash(dataPath), Properties = properties,
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                ProcessorCount = Environment.ProcessorCount, TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Diagnostics = loader.Diagnostics.ToArray(), Projects = loader.Settings,
                loader.OpenSeconds, loader.CompilationSeconds, index.BuildSeconds,
                TotalSeconds = total, QuerySeconds = results.Sum(r => r.Seconds),
                GateSubsetSeconds = loader.OpenSeconds + loader.CompilationSeconds + index.BuildSeconds
                    + results.Where(r => data.Queries.Any(q => q.Id == r.Id && q.OperatorNumber is >= 8 and <= 10)).Sum(r => r.Seconds),
                PeakWorkingSetBytes = peak, BuildHostPeakBytes = (long?)null,
                BuildHostMemoryLimitation = "Child peak not observed; memory limit evaluated on spike process only.",
                index.TestClassCount, NodeCount = index.Symbols.Count, EdgeCount = index.Reverse.Sum(p => p.Value.Count),
                index.IgnoredNameofReferences, index.FrameworkDispatchEdges,
                FixtureChecks = fixtureChecks, Results = results, FrameworkDispatchVariant = variant,
                CostClass = cost, Verdict = verdict
            });
            Console.WriteLine($"{(failed ? "checks failed" : "passed")}; queries={results.Count(r => r.Score == "correct")}/{results.Length}; total={total:F3}s; peak={peak}; verdict={verdict}; receipt={receipt}");
            return failed ? 1 : 0;
        }
        catch (Exception error)
        {
            if (receipt is not null) Write(receipt, paths, new { Status = "analysis failure", Error = error.ToString(), Verdict = "stop" });
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 2;
        }
    }

    private static void Write(string path, ReceiptPaths paths, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, paths.Serialize(value));
    }
}
