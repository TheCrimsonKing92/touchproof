using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: fixture-local SQLite/files, injected changed paths and time; no Git process.
public sealed class BoardFillStaleHeadRevalidationTests
{
    [Fact]
    public void Unrelated_changes_admit_and_persist_the_revalidated_reason()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        h.Head = new string('b', 40);
        var reads = new List<(string From, string To)>();
        h.ChangedPaths = (from, to) => { reads.Add((from, to)); return ["docs/guide.md", "src/Other/File.cs"]; };

        var result = h.Host.AttemptFiling(draft.Id);

        Assert.Equal("filed", result.Result);
        Assert.Equal("ok:revalidated-main-moved", result.Reason);
        Assert.True(result.Terminal);
        Assert.Equal((draft.MainHead!, h.Head!), Assert.Single(reads));
        Assert.Equal(draft.Id, Assert.Single(h.Intake.Requests).DraftId);
        var stored = Assert.Single(h.Drafts.Store.ReadAll());
        Assert.False(stored.StaleHead);
        var receipt = Assert.Single(stored.Filings!);
        Assert.Equal(result.Id, receipt.Id);
        Assert.Equal("filed", receipt.Result);
        Assert.Equal("ok:revalidated-main-moved", receipt.Reason);
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Host.ServiceTick(h.Drafts.Kernel);
        Assert.Contains("reason=ok:revalidated-main-moved", Assert.Single(h.FilingEvents()));

        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.File };
        Assert.Equal("ok:revalidated-main-moved", h.Host.AttemptFiling(draft.Id).Reason);
        Assert.Single(reads);
        Assert.Single(h.Intake.Requests);
    }

    [Theory]
    [InlineData("case-match")]
    [InlineData("backslash-change")]
    [InlineData("backslash-citation")]
    [InlineData("later-verdict")]
    [InlineData("null-read")]
    [InlineData("empty-read")]
    [InlineData("blank-entry")]
    [InlineData("null-entry")]
    [InlineData("no-verdicts")]
    [InlineData("blank-citation")]
    [InlineData("malformed-citation")]
    public void Incomplete_or_overlapping_comparison_refuses_and_marks_stale(string fault)
    {
        using var h = new BoardFillFilingTestFixture();
        h.Head = new string('b', 40);
        IReadOnlyList<string>? changed = ["docs/guide.md"];
        var verification = BoardFillAssessmentTestFixture.Verified;
        switch (fault)
        {
            case "case-match": changed = ["SRC/FEATURE/FILE.CS"]; break;
            case "backslash-change": changed = [@"src\Feature\File.cs"]; break;
            case "backslash-citation":
                changed = ["src/Feature/File.cs"];
                verification = new("complete", 1, [new(1, "verified", @"src\Feature\File.cs:1")]);
                break;
            case "later-verdict":
                changed = ["src/Second.cs"];
                verification = new("complete", 2, [new(1, "verified", "src/Feature/File.cs:1"),
                    new(2, "unverifiable", "src/Second.cs:3-4")]);
                break;
            case "null-read": changed = null; break;
            case "empty-read": changed = []; break;
            case "blank-entry": changed = ["docs/guide.md", " "]; break;
            case "null-entry": changed = ["docs/guide.md", null!]; break;
            case "no-verdicts": verification = new("complete", 0, []); break;
            case "blank-citation": verification = new("complete", 1, [new(1, "verified", "")]); break;
            case "malformed-citation":
                verification = new("complete", 2, [new(1, "verified", "src/Feature/File.cs:1"),
                    new(2, "verified", ":2-3")]);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        h.ChangedPaths = (_, _) => changed;
        var draft = h.Assessed(verification: verification);

        var result = h.Host.AttemptFiling(draft.Id);

        Assert.Equal("refused", result.Result);
        Assert.Equal("stale-head", result.Reason);
        Assert.True(result.Terminal);
        Assert.Empty(result.Stdout);
        Assert.Empty(result.Stderr);
        var stored = Assert.Single(h.Drafts.Store.ReadAll());
        Assert.True(stored.StaleHead);
        Assert.Equal("stale-head", Assert.Single(stored.Filings!).Reason);
        Assert.Empty(h.Intake.Requests);
    }

    [Fact]
    public void Citation_paths_preserve_spaces_and_normalize_separators()
    {
        using var h = new BoardFillFilingTestFixture();
        h.Head = new string('b', 40);
        var draft = h.Assessed(verification: new("complete", 1, [new(1, "verified", @".\src\A File.cs:2-4")]));
        h.ChangedPaths = (_, _) => ["src/AFile.cs"];
        Assert.Equal("filed", h.Host.AttemptFiling(draft.Id).Result);

        using var other = new BoardFillFilingTestFixture();
        other.Head = h.Head;
        var matching = other.Assessed(verification: draft.Assessment!.Verification);
        other.ChangedPaths = (_, _) => ["SRC/A FILE.CS"];
        Assert.Equal("stale-head", other.Host.AttemptFiling(matching.Id).Reason);
    }

    [Fact]
    public void Already_stale_draft_is_not_revalidated()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        var prior = h.Drafts.Store.BeginFiling(draft.Id, h.Drafts.Clock.UtcNow);
        h.Drafts.Store.FinishFiling(draft.Id, prior with { Result = "refused", Reason = "stale-head",
            FinishedAt = h.Drafts.Clock.UtcNow });
        h.Head = new string('b', 40);
        h.ChangedPaths = (_, _) => throw new InvalidOperationException("Stale drafts must skip the read.");

        var result = h.Host.AttemptFiling(draft.Id);

        Assert.Equal("stale-head", result.Reason);
        Assert.Empty(result.Stderr);
        Assert.Empty(h.Intake.Requests);
    }

    [Fact]
    public void Matching_heads_skip_revalidation_and_keep_the_original_success_reason()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        h.ChangedPaths = (_, _) => throw new InvalidOperationException("Matching heads need no diff.");

        var result = h.Host.AttemptFiling(draft.Id);

        Assert.Equal("filed", result.Result);
        Assert.Equal("ok", result.Reason);
        Assert.Single(h.Intake.Requests);
    }

    [Fact]
    public void Changed_path_exception_preserves_retryable_admission_failure()
    {
        using var h = new BoardFillFilingTestFixture();
        var draft = h.Assessed();
        h.Head = new string('b', 40);
        h.ChangedPaths = (_, _) => throw new IOException("read broke");

        var result = h.Host.AttemptFiling(draft.Id);

        Assert.Equal("refused", result.Result);
        Assert.Equal("main-head-unavailable", result.Reason);
        Assert.False(result.Terminal);
        Assert.False(Assert.Single(h.Drafts.Store.ReadAll()).StaleHead);
        Assert.Contains("read broke", result.Stderr);
        Assert.Empty(h.Intake.Requests);
    }

    [Theory]
    [InlineData(false, "custom-success")]
    [InlineData(true, "dependency-failed:bbbbbbbb")]
    public void Revalidated_admission_preserves_other_intake_and_dependency_reasons(bool failedDependency, string expected)
    {
        using var h = new BoardFillFilingTestFixture();
        h.Head = new string('b', 40);
        h.ChangedPaths = (_, _) => ["docs/guide.md"];
        var draft = h.Assessed(scope: new("overlap", [new("bbbbbbbb1111", "scope")]));
        h.Intake.Filing = _ => new("filed", "aaaaaaaa1111", "", "", 0, "custom-success");
        if (failedDependency) h.Intake.Dependency = _ => new("failed", null, "", "failure", 1);

        var result = h.Host.AttemptFiling(draft.Id);

        Assert.Equal("filed", result.Result);
        Assert.Equal(expected, result.Reason);
        Assert.Equal(!failedDependency, Assert.Single(result.Dependencies!).Applied);
    }
}
