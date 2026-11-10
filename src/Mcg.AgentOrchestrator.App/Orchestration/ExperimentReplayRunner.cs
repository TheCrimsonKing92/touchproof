using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ExperimentReplayRunner
{
    internal sealed record Prepared(string Checkout, string BuildArtifacts);
    internal sealed class PreparationInterrupted(ExperimentReplayProcess.Result result,
        IReadOnlyList<ExperimentReplaySource> sources) : Exception(result.Reason ?? result.State)
    {
        internal ExperimentReplayProcess.Result Result { get; } = result;
        internal IReadOnlyList<ExperimentReplaySource> Sources { get; } = sources;
        internal string? CleanupFailure { get; set; }
    }

    internal static void RequirePreparationSuccess(ExperimentReplayProcess.Result result,
        IReadOnlyList<ExperimentReplaySource> sources, string failure)
    {
        if (result.State is "cancelled" or "timed-out") throw new PreparationInterrupted(result, sources);
        if (result.State != "exited" || result.ExitCode != 0) throw new InvalidDataException(failure);
    }
    internal interface IHost
    {
        Task<Prepared> PrepareAsync(ExperimentReplayCase item, string artifacts, CancellationToken token);
        Task<ExperimentReplayExecution> ExecuteAsync(ExperimentReplayRegistration registration, ExperimentReplayRun run,
            ExperimentReplaySlot slot, Prepared prepared, string artifacts, CancellationToken token);
        void Cleanup(Prepared prepared);
    }

    internal static async Task<ExperimentReplayRun> RunAsync(ExperimentReplayStore store, ExperimentReplayRegistration registration,
        string root, IHost host, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        // One bounded shadow execution per project. The open OS handle owns the lease lifetime.
        using var lease = new FileStream(Path.Combine(root, "execution.lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var run = new ExperimentReplayRun(Guid.NewGuid().ToString("N"), registration.Id, "running", null, DateTimeOffset.UtcNow, null);
        store.Start(run);
        var prepared = new Dictionary<string, Prepared>();
        var executions = new List<ExperimentReplayExecution>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(registration.RunTimeoutSeconds));
        var state = "complete";
        string? reason = null;
        try
        {
            var schedule = ExperimentReplaySchedule.Create(registration);
            ExperimentReplayEvidence.Write(Path.Combine(root, run.Id), JsonSerializer.Serialize(schedule, ExperimentReplayStore.Json), ".schedule.json");
            var aaEvaluated = false;
            foreach (var slot in schedule)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (slot.Stage == "AB" && !aaEvaluated)
                {
                    aaEvaluated = true;
                    var calibration = ExperimentReplayStatistics.Calibrate(executions, registration.Seed);
                    if (calibration.State != "passed") { state = "inconclusive"; reason = calibration.Reason; break; }
                }
                if (!prepared.TryGetValue(slot.CaseId, out var candidate))
                {
                    try
                    {
                        candidate = await host.PrepareAsync(registration.Cases.Single(c => c.Id == slot.CaseId),
                            Path.Combine(root, run.Id, slot.CaseId), deadline.Token);
                    }
                    catch (PreparationInterrupted ex)
                    {
                        var interrupted = new ExperimentReplayExecution(Guid.NewGuid().ToString("N"), run.Id, slot,
                            ex.Result.State, ex.Message, HostIdentity(), registration.RuntimeIdentity, registration.ToolchainIdentity,
                            ex.Result.Priority, ex.Result.PowerThrottling, DateTimeOffset.UtcNow,
                            ex.Result.Resources, null, [], ex.Sources);
                        store.Record(interrupted);
                        executions.Add(interrupted);
                        state = ex.CleanupFailure is null ? "incomplete" : "apparatus-failed";
                        reason = ex.CleanupFailure is null ? ex.Message : $"{ex.Message}; cleanup: {ex.CleanupFailure}";
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var unavailable = new ExperimentReplayExecution(Guid.NewGuid().ToString("N"), run.Id, slot,
                            "nonreplayable", ex.Message, HostIdentity(), registration.RuntimeIdentity, registration.ToolchainIdentity,
                            "unavailable", "unavailable", DateTimeOffset.UtcNow, null, null, [], []);
                        store.Record(unavailable);
                        executions.Add(unavailable);
                        state = "inconclusive";
                        reason = $"Case preparation unavailable; operator review required: {ex.Message}";
                        break;
                    }
                    prepared.Add(slot.CaseId, candidate);
                }
                var execution = await host.ExecuteAsync(registration, run, slot, candidate,
                    Path.Combine(root, run.Id, slot.CaseId, slot.Ordinal.ToString("D4")), deadline.Token);
                if (execution.RunId != run.Id || execution.Slot != slot)
                    throw new InvalidDataException("Execution receipt does not match the scheduled run/case/procedure.");
                store.Record(execution);
                executions.Add(execution);
                if (execution.State is "cancelled" or "timed-out")
                { state = "incomplete"; reason = execution.Reason; break; }
                // Invalid measurements are retained in place and are never automatically replaced.
                if (!ExperimentReplayStatistics.Valid(execution))
                { state = "inconclusive"; reason = execution.Reason ?? "Invalid measurement; operator review required."; break; }
            }
            if (state == "complete")
            {
                // Reproduce historical red labels independently after selection measurements.
                foreach (var item in registration.Cases.Where(c => c.Gates.Any(g => g.Outcome == "test-failed")))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var slot = new ExperimentReplaySlot(schedule.Count + executions.Count, item.Id, "reference", 0, "B", false);
                    var reference = await host.ExecuteAsync(registration, run, slot, prepared[item.Id],
                        Path.Combine(root, run.Id, item.Id, "reference"), deadline.Token);
                    store.Record(reference);
                    executions.Add(reference);
                    if (reference.State is "cancelled" or "timed-out")
                    { state = "incomplete"; reason = reference.Reason; break; }
                }
            }
        }
        catch (OperationCanceledException) { state = token.IsCancellationRequested ? "cancelled" : "incomplete"; reason = "Run cancellation/deadline; no retry replacement."; }
        catch (Exception ex) { state = "apparatus-failed"; reason = ex.Message; }
        finally
        {
            foreach (var candidate in prepared.Values)
            {
                try { host.Cleanup(candidate); }
                catch (Exception ex) { state = "apparatus-failed"; reason = $"{reason}; cleanup: {ex.Message}"; }
            }
            run = run with { State = state, Reason = reason, CompletedAt = DateTimeOffset.UtcNow };
            store.Finish(run);
        }
        return run;
    }

    internal sealed class LocalHost(string repository, string dotnet, string runtimeAssembly,
        IReadOnlyList<ExperimentReplaySource>? patches = null, string? toolchainIdentity = null) : IHost
    {
        private readonly Dictionary<string, ExperimentReplayCheckoutLease> _leases = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _checkoutRoot = OrchestratorTempRoot.GetPurposeDirectory("experiment-replay");

        public async Task<Prepared> PrepareAsync(ExperimentReplayCase item, string artifacts, CancellationToken token)
        {
            var checkout = Path.Combine(_checkoutRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
            var added = GitCli.RunForRepository(repository, "-c", "core.hooksPath=" + Path.Combine(checkout, "no-hooks"),
                "worktree", "add", "--detach", "--quiet", checkout, item.CandidateCommit!);
            if (!added.ProcessStarted || !added.Succeeded || added.DrainTimedOut) throw new InvalidDataException($"Replay checkout failed: {added.Error}");
            var prepared = new Prepared(checkout, Path.Combine(artifacts, "build"));
            try
            {
                _leases.Add(checkout, ExperimentReplayCheckoutLease.Create(checkout, item.CandidateCommit!, _checkoutRoot));
                var sources = new List<ExperimentReplaySource>();
                var version = await ExperimentReplayProcess.RunAsync(dotnet, ["--version"], checkout,
                    Path.Combine(artifacts, "toolchain"), TimeSpan.FromMinutes(1), token);
                Retain(version, Path.Combine(artifacts, "toolchain"));
                RequirePreparationSuccess(version, sources, "Historical toolchain probe unavailable.");
                if (toolchainIdentity is not null &&
                    ExperimentReplayStore.Hash(ExperimentReplayStore.Source(dotnet).Sha256 + File.ReadAllText(version.StdoutPath).Trim()) != toolchainIdentity)
                    throw new InvalidDataException("Historical toolchain differs from the registered installed SDK.");
                var head = GitCli.RunForRepository(checkout, "rev-parse", "HEAD");
                if (!head.Succeeded || head.Output.Trim() != item.CandidateCommit) throw new InvalidDataException("Replay checkout identity mismatch.");
                ExperimentReplayProcedure.ApplyPatches(checkout, patches ?? []);
                var routing = new ImpactReplayChangedSourceRouting(checkout);
                foreach (var project in routing.TestProjects)
                {
                    var relative = Path.GetRelativePath(checkout, project.Path);
                    // The actual prerequisite boundary is separate and retained, including restore/build descendants.
                    var result = await ExperimentReplayProcess.RunAsync(dotnet,
                        ["build", relative, "--artifacts-path", prepared.BuildArtifacts, "--nologo", "-v:q", "-p:RunAnalyzers=false"],
                        checkout, Path.Combine(artifacts, "prerequisites", ExperimentReplayStore.Hash(relative)[..12]), TimeSpan.FromMinutes(20), token);
                    Retain(result, Path.Combine(artifacts, "prerequisites"));
                    RequirePreparationSuccess(result, sources, $"Historical build unavailable: {relative}; {result.Reason}");
                }
                return prepared;

                void Retain(ExperimentReplayProcess.Result result, string directory)
                {
                    sources.Add(ExperimentReplayEvidence.Write(directory, JsonSerializer.Serialize(result, ExperimentReplayStore.Json), ".json"));
                    sources.Add(ExperimentReplayStore.Source(result.StdoutPath));
                    sources.Add(ExperimentReplayStore.Source(result.StderrPath));
                }
            }
            catch (PreparationInterrupted ex)
            {
                try { Cleanup(prepared); }
                catch (Exception cleanup) { ex.CleanupFailure = cleanup.Message; }
                throw;
            }
            catch { Cleanup(prepared); throw; }
        }

        public async Task<ExperimentReplayExecution> ExecuteAsync(ExperimentReplayRegistration registration, ExperimentReplayRun run,
            ExperimentReplaySlot slot, Prepared prepared, string artifacts, CancellationToken token)
        {
            Directory.CreateDirectory(artifacts);
            var item = registration.Cases.Single(c => c.Id == slot.CaseId);
            if (RuntimeIdentity(Path.GetDirectoryName(runtimeAssembly)!) != registration.RuntimeIdentity)
                throw new InvalidDataException("Registered runtime bytes changed.");
            var request = new ExperimentReplayProcedure.Request(prepared.Checkout, item.BaseCommit!, item.CandidateCommit!,
                slot.Stage == "AA" ? "B" : slot.Arm, dotnet, prepared.BuildArtifacts,
                Path.Combine(artifacts, "results"), Path.Combine(artifacts, "completion.json"), slot.Stage == "reference", patches, _checkoutRoot);
            var source = ExperimentReplayEvidence.Write(artifacts, JsonSerializer.Serialize(request, ExperimentReplayStore.Json), ".request.json");
            var load = ExperimentReplayEvidence.Write(artifacts, JsonSerializer.Serialize(new
            {
                processorCount = Environment.ProcessorCount,
                availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                hostCpuLoad = "unavailable; no contention inference",
                resetPolicy = registration.ResetPolicy
            }, ExperimentReplayStore.Json), ".host.json");
            var startedAt = DateTimeOffset.UtcNow;
            var result = await ExperimentReplayProcess.RunAsync(dotnet, [runtimeAssembly, ExperimentReplayProcedure.Command, source.Identity],
                prepared.Checkout, artifacts, TimeSpan.FromSeconds(registration.ProcedureTimeoutSeconds), token);
            var sources = new List<ExperimentReplaySource> { source, load, ExperimentReplayStore.Source(result.StdoutPath), ExperimentReplayStore.Source(result.StderrPath) };
            ExperimentReplayProcedure.Completion? completion = null;
            if (File.Exists(request.CompletionPath))
            {
                completion = JsonSerializer.Deserialize<ExperimentReplayProcedure.Completion>(File.ReadAllText(request.CompletionPath), ExperimentReplayStore.Json);
                sources.Add(ExperimentReplayStore.Source(request.CompletionPath));
                if (completion is not null) sources.AddRange(completion.Sources);
            }
            return new(Guid.NewGuid().ToString("N"), run.Id, slot,
                result.State == "exited" && result.ExitCode == 0 && completion is not null ? completion.State : result.State == "exited" ? "apparatus-failed" : result.State,
                result.Reason ?? completion?.Reason ?? (completion is null ? "Required completion artifact missing." : null),
                HostIdentity(), registration.RuntimeIdentity, registration.ToolchainIdentity, result.Priority, result.PowerThrottling,
                startedAt, result.Resources, completion?.Selection, completion?.FailingTests ?? [], sources);
        }

        public void Cleanup(Prepared prepared)
        {
            if (!_leases.TryGetValue(prepared.Checkout, out var lease))
                throw new InvalidDataException("Replay cleanup requires this host's live checkout lease.");
            try
            {
                using var ownership = ExperimentReplayCheckoutLease.Acquire(prepared.Checkout, lease.CandidateCommit, lease.CheckoutRoot);
                var removed = GitCli.RunForRepository(repository, "worktree", "remove", "--force", prepared.Checkout);
                if (!removed.ProcessStarted || !removed.Succeeded || removed.DrainTimedOut || Directory.Exists(prepared.Checkout))
                    throw new IOException($"Owned replay worktree cleanup failed: {prepared.Checkout}; {removed.Error}");
            }
            finally
            {
                _leases.Remove(prepared.Checkout);
                lease.Dispose();
            }
        }
    }

    internal static string RuntimeIdentity(string directory) => ExperimentReplayStore.Hash(string.Join("\n",
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p) is ".dll" or ".json" or ".exe")
            .OrderBy(p => Path.GetRelativePath(directory, p), StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(directory, p) + ":" + ExperimentReplayStore.Source(p).Sha256)));
    internal static string HostIdentity() => ExperimentReplayStore.Hash($"{Environment.MachineName}/{Environment.OSVersion}/{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
}
