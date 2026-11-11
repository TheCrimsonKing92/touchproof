using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentReplayFaultRunner
{
    internal sealed record Definition(string CaseId, string Shape, string PatchPath,
        IReadOnlyList<string> ExpectedTests, string? DefectPatchPath = null);
    internal static readonly string[] Shapes = ["reflection-signature", "deleted-assertion-plus-defect", "renamed-fixture"];

    internal static ExperimentReplayFault Evaluate(string id, ExperimentReplayRun run, ExperimentReplayRegistration registration,
        Definition definition, IReadOnlyList<ExperimentReplaySource> patches, IReadOnlyList<ExperimentReplayExecution> executions)
    {
        var control = executions.SingleOrDefault(e => e.Slot.Stage == "control");
        var reference = executions.SingleOrDefault(e => e.Slot.Stage == "reference");
        var originalAssertion = executions.SingleOrDefault(e => e.Slot.Stage == "defect-control");
        var controlValid = control is not null && ExperimentReplayStatistics.Valid(control) && control.State == "passed";
        var assertionValid = definition.Shape != "deleted-assertion-plus-defect" ||
            originalAssertion is not null && ExperimentReplayStatistics.Valid(originalAssertion) &&
            definition.ExpectedTests.Intersect(originalAssertion.FailingTests, StringComparer.Ordinal).Any();
        var confirmed = controlValid && assertionValid && reference is not null && ExperimentReplayStatistics.Valid(reference)
            ? definition.ExpectedTests.Intersect(reference.FailingTests, StringComparer.Ordinal).ToArray() : [];
        var catchesA = confirmed.Where(t => executions.Any(e => e.Slot.Stage == "mutant" && e.Slot.Arm == "A" &&
            ExperimentReplayStatistics.Valid(e) && e.FailingTests.Contains(t, StringComparer.Ordinal))).ToArray();
        var catchesB = confirmed.Where(t => executions.Any(e => e.Slot.Stage == "mutant" && e.Slot.Arm == "B" &&
            ExperimentReplayStatistics.Valid(e) && e.FailingTests.Contains(t, StringComparer.Ordinal))).ToArray();
        return new(id, run.Id, definition.CaseId, definition.Shape, registration.Seed,
            registration.Cases.Single(c => c.Id == definition.CaseId).CandidateCommit!,
            confirmed.Length > 0 ? "positive" : "unsupported/nonpositive",
            confirmed.Length > 0 ? "Unmodified control passes and independent reference reproduces intended defect."
                : "Required control or discriminating oracle evidence unavailable; excluded from catches and misses.",
            patches, definition.ExpectedTests, executions, confirmed, catchesA, catchesB);
    }

    internal static async Task<IReadOnlyList<ExperimentReplayFault>> RunAsync(ExperimentReplayStore store,
        ExperimentReplayRegistration registration, ExperimentReplayRun run, IReadOnlyList<Definition> definitions,
        string root, string repository, string dotnet, string runtimeAssembly, CancellationToken token)
    {
        if (definitions.Count != 3 || !definitions.Select(d => d.Shape).Order().SequenceEqual(Shapes.Order()) ||
            definitions.Select(d => d.CaseId).Distinct().Count() != 3 || definitions.Any(d =>
                !registration.Cases.Any(c => c.Id == d.CaseId) || d.ExpectedTests.Count == 0 ||
                d.Shape == "deleted-assertion-plus-defect" && string.IsNullOrWhiteSpace(d.DefectPatchPath)))
            throw new InvalidDataException("Injection requires three registered cases, the three fault shapes, patches and explicit oracles.");
        if (run.State != "complete" || ExperimentReplayStatistics.Calibrate(store.Executions(run.Id), registration.Seed).State != "passed")
            throw new InvalidOperationException("Historical pilot/calibration stop requires operator review before injection.");
        if (store.Faults(run.Id).Count > 0) throw new InvalidOperationException("Injection outcomes already exist; no result-dependent reruns.");
        using var lease = new FileStream(Path.Combine(root, "execution.lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(4));
        var results = new List<ExperimentReplayFault>();
        foreach (var definition in definitions)
        {
            var id = Guid.NewGuid().ToString("N");
            var artifacts = Path.Combine(root, run.Id, "faults", id);
            var patches = new List<ExperimentReplaySource>();
            var executions = new List<ExperimentReplayExecution>();
            string? failure = null;
            try
            {
                if (definition.DefectPatchPath is { } defect)
                    patches.Add(ExperimentReplayEvidence.Copy(Path.Combine(artifacts, "patches"), defect));
                patches.Add(ExperimentReplayEvidence.Copy(Path.Combine(artifacts, "patches"), definition.PatchPath));
                await Variant("control", [], true);
                if (definition.Shape == "deleted-assertion-plus-defect") await Variant("defect-control", patches.Take(1).ToArray(), true);
                await Variant("mutant", patches, false);
            }
            catch (Exception ex) { failure = ex.Message; }
            var result = Evaluate(id, run, registration, definition, patches, executions);
            if (failure is not null) result = result with { State = "apparatus-failed", Reason = failure, ConfirmedDefects = [], ACatches = [], BCatches = [] };
            store.RecordFault(result);
            results.Add(result);
            if (failure is not null) break;

            async Task Variant(string stage, IReadOnlyList<ExperimentReplaySource> applied, bool referenceOnly)
            {
                var host = new ExperimentReplayRunner.LocalHost(repository, dotnet, runtimeAssembly, applied, registration.ToolchainIdentity);
                var item = registration.Cases.Single(c => c.Id == definition.CaseId);
                var prepared = await host.PrepareAsync(item, Path.Combine(artifacts, stage), deadline.Token);
                try
                {
                    foreach (var arm in referenceOnly ? new[] { "B" } : ["A", "B", "reference"])
                    {
                        var slot = new ExperimentReplaySlot(executions.Count, item.Id,
                            arm == "reference" ? "reference" : stage, 0, arm == "reference" ? "B" : arm, false);
                        // Controls/oracles always execute the full reference inventory.
                        var execution = await host.ExecuteAsync(registration, run,
                            referenceOnly ? slot with { Stage = "reference" } : slot, prepared,
                            Path.Combine(artifacts, stage, arm), deadline.Token);
                        if (referenceOnly) execution = execution with { Slot = slot };
                        executions.Add(execution);
                        ExperimentReplayEvidence.Write(Path.Combine(artifacts, "executions"),
                            JsonSerializer.Serialize(execution, ExperimentReplayStore.Json), ".json");
                        if (!ExperimentReplayStatistics.Valid(execution)) throw new InvalidDataException(execution.Reason ?? "Invalid fault execution.");
                    }
                }
                finally { host.Cleanup(prepared); }
            }
        }
        return results;
    }
}
