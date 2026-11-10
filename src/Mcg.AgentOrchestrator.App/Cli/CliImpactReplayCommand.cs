using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliImpactReplayCommand
{
    private static bool IsCancellation(Exception error) => error is OperationCanceledException ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Count > 0 && IsCancellation(aggregate.InnerExceptions[0]);

    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("impact-replay", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return Run(args, workspace.ExecutionDirectory, workspace.OrchestratorDirectory, null,
            Console.Out, Console.Error, cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
    }

    // Explicit directories let fixture tests exercise the command without opening live stores.
    internal static int Run(IReadOnlyList<string> args, string executionDirectory, string orchestratorDirectory,
        string? tempCheckoutParent, TextWriter output, TextWriter error, CancellationToken cancellationToken = default,
        TimeProvider? clock = null, ImpactReplayExecutionSource? source = null)
    {
        if (args.Count > 1 && args[1] == "--status")
        {
            if (args.Count != 4 || args[2] != "--checkpoint-file" || string.IsNullOrWhiteSpace(args[3])) return Usage();
            try
            {
                output.WriteLine(ImpactReplayCheckpointStore.Serialize(ImpactReplayCheckpointStore.ReadStatus(args[3])));
                return 0;
            }
            catch (Exception ex) { error.WriteLine($"Impact replay failed: {ex.Message}"); return 1; }
        }
        DateTimeOffset? since = null;
        var limit = 100;
        int? maxChangedSourceFiles = null;
        string? destination = null;
        var floor = new List<string>();
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                output.WriteLine(CliCommandHelp.ImpactReplayUsage);
                output.WriteLine(CliCommandHelp.ImpactReplayStatusUsage);
                return 0;
            }
            if (args[i] is not ("--since" or "--limit" or "--out" or "--floor" or "--max-changed-source-files") || i + 1 == args.Count)
                return Usage();
            var flag = args[i++];
            var value = args[i];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)) return Usage();
            switch (flag)
            {
                case "--since":
                    if (!DateTimeOffset.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return Usage();
                    since = date;
                    break;
                case "--limit":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1) return Usage();
                    break;
                case "--out": destination = value; break;
                case "--max-changed-source-files":
                    if (maxChangedSourceFiles is not null ||
                        !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) || maximum < 1) return Usage();
                    maxChangedSourceFiles = maximum;
                    break;
                case "--floor": floor.Add(value); break;
            }
        }
        if (destination is null) return Usage();
        ImpactReplayRunSink? sink = null;
        try
        {
            clock ??= TimeProvider.System;
            var configuration = new ImpactReplayRunConfiguration(Path.GetFullPath(executionDirectory),
                Path.GetFullPath(orchestratorDirectory), since, limit, floor.Count == 0 ? null : floor.ToArray(),
                maxChangedSourceFiles ?? 5, tempCheckoutParent is null ? null : Path.GetFullPath(tempCheckoutParent));
            var assembly = typeof(ImpactReplay).Assembly;
            source ??= new ImpactReplayExecutionSource();
            var revision = source.ReadSourceRevision(executionDirectory);
            var identity = new ImpactReplayRunIdentity(Guid.NewGuid().ToString("N"), clock.GetUtcNow(), configuration,
                RuntimeInformation.FrameworkDescription + "; " + RuntimeInformation.OSDescription,
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion +
                    "; mvid=" + assembly.ManifestModule.ModuleVersionId,
                revision);
            sink = new ImpactReplayRunSink(destination, identity, error, clock);
            var report = ImpactReplay.Read(executionDirectory, orchestratorDirectory, since, limit,
                floor.Count == 0 ? null : floor, tempCheckoutParent, maxChangedSourceFiles,
                sink, cancellationToken, source);
            report = report with { Run = new(sink.RunId, sink.CheckpointPath) };
            cancellationToken.ThrowIfCancellationRequested();
            sink.FinalizeReport(report);
            ImpactReplayReportWriter.Write(destination, report);
            cancellationToken.ThrowIfCancellationRequested();
            sink.Complete();
            output.WriteLine($"Impact replay: {report.Attempts.Count} attempts; reports: {Path.GetFullPath(destination)}");
            return 0;
        }
        catch (Exception ex)
        {
            sink?.Fail(ex, IsCancellation(ex) && cancellationToken.IsCancellationRequested);
            error.WriteLine($"Impact replay failed: {ex}");
            error.Flush();
            return 1;
        }
        finally { sink?.Dispose(); }
        int Usage() { error.WriteLine(CliCommandHelp.ImpactReplayUsage); return 2; }
    }
}
