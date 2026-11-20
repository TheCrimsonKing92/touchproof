using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

using static ConductorDriverTests;

public sealed class ReviewerFindingsParseFixtureTests
{
    [Xunit.Fact]
    public void IntakeNormalizesInsideLocationsAndPreservesOutsideFindingText()
    {
        var root = Path.Combine(Path.GetTempPath(), "finding-intake", Guid.NewGuid().ToString("N"));
        var outsidePath = Path.Combine(root + "-evidence", "round.result.json").Replace('\\', '/');
        var insidePath = Path.Combine(root, "src", "Inside.cs");
        var findings = new[]
        {
            new ReviewFinding("outside", ReviewFindingState.Open,
                new(outsidePath, "round", "receipt"), "Evidence is incomplete.", FindingSeverity.Advisory),
            new ReviewFinding("inside", ReviewFindingState.Open,
                new(insidePath, "Inside.Run", "guard"), "Guard is missing."),
            new ReviewFinding("traversal", ReviewFindingState.Resolved,
                new("src/../../outside.json", "traversal"), "Keep the relative location."),
            new ReviewFinding("relative", ReviewFindingState.Open,
                new("./src\\Inside.cs", "Inside.Run"), "Keep the relative identity."),
            new ReviewFinding("already-described", ReviewFindingState.Open,
                new(outsidePath, "other-round"), $"Check {outsidePath} for details.")
        };

        var round = ReadFindingRound(root, findings);

        Xunit.Assert.Equal(findings.Length, round.Findings.Count);
        var outside = round.Findings.Single(finding => finding.StableId == "outside");
        Xunit.Assert.Contains(findings[0].Description, outside.Description, StringComparison.Ordinal);
        Xunit.Assert.Contains(outsidePath, outside.Description, StringComparison.Ordinal);
        Xunit.Assert.Equal(findings[0] with
        {
            Location = findings[0].Location with { File = ReviewFindingConvergence.NoFileAnchor },
            Description = outside.Description
        }, outside);
        Xunit.Assert.Equal(findings[1] with
        {
            Location = findings[1].Location with { File = "src/Inside.cs" }
        }, round.Findings.Single(finding => finding.StableId == "inside"));
        var traversal = round.Findings.Single(finding => finding.StableId == "traversal");
        Xunit.Assert.Equal(ReviewFindingConvergence.NoFileAnchor, traversal.Location.File);
        Xunit.Assert.Contains(findings[2].Location.File, traversal.Description, StringComparison.Ordinal);
        Xunit.Assert.Equal(findings[3], round.Findings.Single(finding => finding.StableId == "relative"));
        Xunit.Assert.Equal(findings[4].Description,
            round.Findings.Single(finding => finding.StableId == "already-described").Description);
    }

    [Xunit.Fact]
    public void IntakeUnusableLocationsKeepFindingsWithoutFileAnchors()
    {
        var root = Path.Combine(Path.GetTempPath(), "finding-intake", Guid.NewGuid().ToString("N"));
        var inside = Path.Combine(root, "src", "Inside.cs");
        var cases = new[]
        {
            (Root: "", File: inside),
            (Root: "relative-root", File: inside),
            (Root: root, File: "invalid\0path"),
            (Root: root, File: root),
            (Root: "", File: "../outside.json"),
            (Root: root, File: OperatingSystem.IsWindows() ? "C:drive-relative.cs" : "/outside.cs")
        };
        foreach (var item in cases)
        {
            var finding = new ReviewFinding("unusable", ReviewFindingState.Open,
                new(item.File, "receipt"), "Retain this finding.");

            var recorded = Xunit.Assert.Single(ReadFindingRound(item.Root, [finding]).Findings);

            Xunit.Assert.Equal(ReviewFindingConvergence.NoFileAnchor, recorded.Location.File);
            Xunit.Assert.Contains(item.File, recorded.Description, StringComparison.Ordinal);
            Xunit.Assert.Contains(finding.Description, recorded.Description, StringComparison.Ordinal);
        }
    }

    private static ReviewFindingRound ReadFindingRound(string workingDirectory, ReviewFinding[] findings)
    {
        var verification = new TaskVerificationRecord(
            "review", workingDirectory, 0,
            $"WORKER_RESULT:\nfindings: {JsonSerializer.Serialize(findings)}\ntouched_anchors: []\nEND_WORKER_RESULT",
            string.Empty, DateTimeOffset.Parse("2026-11-07T06:00:00Z"), WorkerResultPresent: true);
        Xunit.Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(
            verification, out var round, out var diagnostic, out var rejections, out var violation), diagnostic);
        Xunit.Assert.Empty(rejections);
        Xunit.Assert.Null(violation);
        return round;
    }

    [Xunit.Theory]
    [Xunit.InlineData(
        "b2f52d39-0d99c73d-20260905153941.out.txt",
        "BBB1EDEA0B4661CBC02FBE6CC03A6612E9E83883E0FD83F6BCB5A1D540DADD3D")]
    [Xunit.InlineData(
        "ae54b5eb-e9ea0560-20260905170025.out.txt",
        "57085A40E6DA99AFECC7B22C04E01798408A3DEE5C5A22997710A46DF0AA08D6")]
    public void RetainedReviewerStdoutParsesOrRecordsTheRejectedFragment(
        string fileName,
        string expectedSourceSha256)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReviewerFindingsParse", fileName);
        var fixtureBytes = File.ReadAllBytes(path);
        Xunit.Assert.Equal(
            expectedSourceSha256,
            Convert.ToHexString(SHA256.HashData(fixtureBytes)));
        var stdout = Encoding.UTF8.GetString(fixtureBytes);

        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            stdout,
            string.Empty,
            DateTimeOffset.Parse("2026-09-05T18:05:00Z"),
            StandardOutputPath: path,
            WorkerResultPresent: true);

        if (WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out _))
        {
            Xunit.Assert.Contains(round.Findings, finding =>
                finding.State == ReviewFindingState.Open && finding.Severity == FindingSeverity.Blocking);
            return;
        }

        Xunit.Assert.False(WorkerResultBlockers.TryFindReviewFindingRound(
            verification,
            out _,
            out var diagnostic));
        Xunit.Assert.True(WorkerResultBlockers.TryReadReviewFindingFragments(
            verification,
            out var findingsFragment,
            out _));
        var (kernel, goal) = SoftwareGoal("Retained Reviewer stdout diagnostic fixture");
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        DispatchTask(kernel, goal, reviewer, "review");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);

        var note = Xunit.Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Rejected Reviewer structured finding result", StringComparison.Ordinal)));
        Xunit.Assert.Contains(diagnostic, note.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(findingsFragment![..Math.Min(80, findingsFragment.Length)], note.Message, StringComparison.Ordinal);
    }
}
