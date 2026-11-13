using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillRoundResult(AuthorBriefDraftOutcome Outcome, string? Markdown,
    BoardFillPremiseVerification Verification, bool RevisionAttempted = false,
    BoardFillRoundFirstPass? FirstPass = null);

internal sealed record BoardFillRoundFirstPass(string Markdown, BoardFillPremiseVerification Verification,
    IReadOnlyList<AuthorBriefDraftCheck> Checks, IReadOnlyList<AuthorBriefDraftCheck> PreflightChecks);

internal sealed partial class ConductorBoardFillHost
{
    private async Task<BoardFillRoundResult> RunRoundAsync(string id)
    {
        // The draft seam's exceptions remain faulted tasks for the existing harvest contract.
        var outcome = _draft(id, _shutdown.Token);
        string? markdown = null;
        if (outcome.Kind == "draft" && outcome.DraftPath is { } path)
        {
            try { markdown = await File.ReadAllTextAsync(path, _shutdown.Token).ConfigureAwait(false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (OperationCanceledException) { }
        }
        var verification = await VerifyPremiseAsync(markdown, outcome.MainHead).ConfigureAwait(false);
        var preflight = BoardFillPreflightChecks.Run(markdown ?? "");
        if (_revise is null || outcome.Kind != "draft" || markdown is null ||
            string.IsNullOrWhiteSpace(outcome.MainHead) || outcome.DraftPath is null ||
            _shutdown.IsCancellationRequested || !BoardFillVerdictRevision.ShouldRevise(verification, preflight))
            return new(outcome, markdown, verification);

        var firstPass = new BoardFillRoundFirstPass(markdown, verification, outcome.Checks, preflight);
        var request = new BoardFillVerdictRevisionRequest(id, outcome.MainHead,
            AuthorBriefDraftPrompt.RenderVerdictRevision(markdown, verification, preflight));
        BoardFillVerdictRevisionResult revision;
        try { revision = await _revise(request, _shutdown.Token).ConfigureAwait(false); }
        catch (Exception) { return new(outcome, markdown, verification, RevisionAttempted: true); }
        if (!BoardFillVerdictRevision.Accept(request, revision) ||
            !await ReplaceDraftAsync(outcome.DraftPath, revision.Markdown!).ConfigureAwait(false))
            return new(outcome, markdown, verification, RevisionAttempted: true);

        // An accepted revision is final, even when its new checks or verification fail.
        outcome = outcome with { Checks = revision.Checks };
        markdown = revision.Markdown!;
        verification = await VerifyPremiseAsync(markdown, request.MainHead).ConfigureAwait(false);
        return new(outcome, markdown, verification, RevisionAttempted: true, FirstPass: firstPass);
    }

    private async Task<BoardFillPremiseVerification> VerifyPremiseAsync(string? markdown, string? mainHead)
    {
        var premise = BoardFillVerifierContract.Premise(markdown ?? "");
        var verification = new BoardFillPremiseVerification("unavailable", BoardFillVerifierContract.BulletCount(premise), [],
            markdown is null ? "draft-unreadable" : mainHead is null ? "missing-main-head" : "verifier-not-configured");
        if (markdown is not null && mainHead is { } head && _verifier is not null)
        {
            try { verification = await _verifier.VerifyAsync(premise, head, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { verification = verification with { Status = "failed", Detail = "cancelled" }; }
            catch (Exception exception) { verification = verification with { Status = "failed", Detail = exception.GetType().Name }; }
        }
        return verification;
    }

    private async Task<bool> ReplaceDraftAsync(string path, string markdown)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, markdown, _shutdown.Token).ConfigureAwait(false);
            _shutdown.Token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (OperationCanceledException) { return false; }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Assess(BoardFillDraftRound round, string? markdown, BoardFillPremiseVerification verification,
        AgentOrchestratorKernel kernel, bool? revisionAttempted = null, BoardFillRoundFirstPass? firstPass = null)
    {
        var items = _backlog();
        var goals = kernel.Goals.ToArray();
        var current = items.FirstOrDefault(item => item.Id == round.BacklogItemId);
        var readiness = _readiness(kernel, items);
        var assessment = Evaluate(round, markdown, verification, BoardFillPreflightChecks.Run(markdown ?? ""));
        // Kernel/backlog state belongs to the tick thread. Evaluate both immutable passes
        // against this same harvest snapshot, preserving every first-pass failing reason.
        var firstAssessment = firstPass is null ? assessment : Evaluate(round with { Checks = firstPass.Checks },
            firstPass.Markdown, firstPass.Verification, firstPass.PreflightChecks);
        _store.Assess(round.Id, assessment, revisionAttempted,
            revisionAttempted is null ? null : firstAssessment.FailingReasons);

        BoardFillDraftAssessment Evaluate(BoardFillDraftRound passRound, string? passMarkdown,
            BoardFillPremiseVerification passVerification, IReadOnlyList<AuthorBriefDraftCheck> preflight) =>
            BoardFillFileability.Evaluate(passRound, passMarkdown, passVerification, preflight,
                BoardFillScopeDepends.Propose(passMarkdown ?? "", goals, round.BacklogItemId), current, goals, readiness);
    }
}
