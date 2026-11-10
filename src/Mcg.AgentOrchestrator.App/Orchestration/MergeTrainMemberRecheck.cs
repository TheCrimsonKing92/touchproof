using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only a complete comparison with both passing and failing members authorizes effects.
internal static class MergeTrainMemberRecheck
{
    internal const string AttributionSource = "member-recheck";

    internal interface IOutcomeCache
    {
        Outcome? Read(MergeTrainReceipt receipt);
        bool TrySave(MergeTrainReceipt receipt, Outcome outcome);
    }

    internal sealed record MemberResult(string GoalId, string CandidateRevision, string BranchRevision,
        int? BriefVersion, string Result, long? CpuMilliseconds);

    internal sealed record Outcome(IReadOnlyList<MemberResult> Members, string? FallbackReason)
    {
        internal IReadOnlyList<MergeTrainMemberBinding> Implicated(MergeTrainReceipt receipt) =>
            FallbackReason is not null ? [] : receipt.Identity.Members.Where(member =>
                Members.Any(result => result.GoalId == member.GoalId.Value && result.Result == "fail")).ToArray();

        internal IReadOnlyList<MergeTrainMemberBinding> Remaining(MergeTrainReceipt receipt) =>
            receipt.Identity.Members.Except(Implicated(receipt)).ToArray();

        internal IReadOnlyList<MergeTrainEjection> Ejections(MergeTrainReceipt receipt) =>
            Implicated(receipt).Select(member => new MergeTrainEjection(member.GoalId,
                MergeTrainEjectionReason.RedAttributedMember, [], $"Dropped after RED receipt {receipt.ReceiptId}.")).ToArray();

        internal string EventKind => Members.Any(member => member.Result is "pass" or "fail")
            ? "train-attribution" : "train-member-recheck";

        internal long? CpuMillisecondsTotal => Members.All(member => member.CpuMilliseconds is not null)
            ? Members.Sum(member => member.CpuMilliseconds!.Value) : null;

        internal string EventDetail(MergeTrainReceipt receipt) =>
            $"MEMBER_RECHECK train={receipt.Identity.Value} receipt={receipt.ReceiptId} attribution={AttributionSource} " +
            $"reason={FallbackReason ?? "attributed"} cpu_ms={CpuText(CpuMillisecondsTotal)} members=[" +
            string.Join(';', Members.Select(member => $"member={member.GoalId} candidate_revision={member.CandidateRevision} " +
                $"result={member.Result} cpu_ms={CpuText(member.CpuMilliseconds)}")) + "]";

        private static string CpuText(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
    }

    internal static Outcome? Run(MergeTrainReceipt receipt, MergeTrainMemberBinding? attributed,
        bool messageSubjectFailure, int attempt, MergeTrainMemberRecheckRunner.Runner runner,
        IOutcomeCache cache, Action<MergeTrainMemberBinding, string> implicate,
        Action<MergeTrainEjection> eject, Action<string, string> log, CancellationToken token)
    {
        if (attributed is not null || messageSubjectFailure || attempt != 0 || receipt.Identity.Members.Count != 3 ||
            receipt.Outcome != MergeTrainGateOutcome.Failed) return null;
        token.ThrowIfCancellationRequested();
        var outcome = cache.Read(receipt);
        var saved = false;
        if (outcome is null)
        {
            if (!MergeTrainRedAttribution.IsGenuineTrainRed(receipt)) return null;
            outcome = Decide(receipt, runner, token);
            token.ThrowIfCancellationRequested();
            // Persist before implication/ejection so replay cannot change the experiment.
            saved = cache.TrySave(receipt, outcome);
            if (!saved) outcome = cache.Read(receipt) ?? throw new InvalidDataException("Member recheck outcome was not saved.");
        }
        Validate(receipt, outcome);
        foreach (var member in outcome.Implicated(receipt)) implicate(member, AttributionSource);
        foreach (var ejection in outcome.Ejections(receipt)) eject(ejection);
        if (saved) log(outcome.EventKind, outcome.EventDetail(receipt));
        return outcome;
    }

    private static Outcome Decide(MergeTrainReceipt receipt, MergeTrainMemberRecheckRunner.Runner runner, CancellationToken token)
    {
        var entries = receipt.Identity.Members.Select(member => new MemberResult(member.GoalId.Value,
            member.CandidateRevision, member.BranchRevision, member.BriefVersion, "unrun", null)).ToArray();
        if (!MergeTrainFocusedRecheckDecision.TryReadFailingIdentities(receipt, out var failing))
            return new(entries, "unreadable-identities");
        // Preserve the original identity bound before deduplicating to whole classes.
        if (MergeTrainFocusedRecheckDecision.Select(failing, []) is null)
            return new(entries, "focused-bounds");
        var classes = failing.Select(identity => identity[..identity.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (MergeTrainFocusedRecheckDecision.Select(failing, classes) is null)
            return new(entries, "focused-bounds");
        var selection = new ConductorCohortFocusedSelection(
            string.Join("; ", classes.Select(name => $"FullyQualifiedName~{name}")), classes);
        for (var index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            MergeTrainFocusedRecheckRunner.RunResult? run;
            try { run = runner(receipt.Identity.Members[index], receipt.Identity.ObservedMainRevision, selection, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { run = null; }
            var refusal = MergeTrainFocusedRecheckDecision.ExecutionRefusal(run, selection);
            var results = run is null ? null : MergeTrainFocusedRecheckRunner.ReadExecution(run.Verification.TestResultPaths);
            var bound = run is not null && run.MainRevision == receipt.Identity.ObservedMainRevision &&
                !string.IsNullOrWhiteSpace(run.Verification.TreeRevision);
            var selectedFailure = results?.Failures.Any(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) &&
                ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is null &&
                selection.Identities.Any(selector => MergeTrainFocusedRecheckDecision.Covers(selector, failure.TestName ?? ""))) == true;
            var verdict = bound && refusal is null ? "pass" :
                bound && refusal == "recheck-failed" && selectedFailure ? "fail" : "unavailable";
            entries[index] = entries[index] with { Result = verdict,
                CpuMilliseconds = run?.CpuMilliseconds is { } cpu && cpu >= 0 ? cpu : null };
            if (verdict == "unavailable") return new(entries, "execution-failure");
        }
        var failed = entries.Count(entry => entry.Result == "fail");
        return new(entries, failed == 0 ? "interaction" : failed == entries.Length ? "all-members-failed" : null);
    }

    internal static void Validate(MergeTrainReceipt receipt, Outcome outcome)
    {
        if (outcome.Members is null || outcome.Members.Count != receipt.Identity.Members.Count ||
            outcome.Members.Zip(receipt.Identity.Members).Any(pair => pair.First is null ||
                pair.First.GoalId != pair.Second.GoalId.Value || pair.First.CandidateRevision != pair.Second.CandidateRevision ||
                pair.First.BranchRevision != pair.Second.BranchRevision || pair.First.BriefVersion != pair.Second.BriefVersion ||
                pair.First.CpuMilliseconds is < 0 || pair.First.Result is not ("pass" or "fail" or "unavailable" or "unrun")))
            throw new InvalidDataException("Stored member recheck does not match its receipt.");
        var failed = outcome.Members.Count(member => member.Result == "fail");
        var complete = outcome.Members.All(member => member.Result is "pass" or "fail");
        var valid = outcome.FallbackReason switch
        {
            null => complete && failed > 0 && failed < outcome.Members.Count,
            "interaction" => complete && failed == 0,
            "all-members-failed" => complete && failed == outcome.Members.Count,
            "unreadable-identities" or "focused-bounds" => outcome.Members.All(member => member.Result == "unrun"),
            "execution-failure" => !complete && outcome.Members.Any(member => member.Result == "unavailable"),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Stored member recheck has an unsafe disposition.");
    }
}
