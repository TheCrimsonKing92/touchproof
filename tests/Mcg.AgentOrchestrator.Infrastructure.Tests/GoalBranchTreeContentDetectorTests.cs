using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all git operations are scripted per test; no processes or shared state.
public sealed class GoalBranchTreeContentDetectorTests
{
    private static readonly string Main = new('1', 40);
    private static readonly string Tip = new('2', 40);
    private static readonly string Base = new('3', 40);
    private static readonly string OldBlob = new('4', 40);
    private static readonly string NewBlob = new('5', 40);
    private static readonly string Missing = new('0', 40);

    [Xunit.Theory]
    [Xunit.InlineData("", "EquivalentToMain")]
    [Xunit.InlineData("same", "EquivalentToMain")]
    [Xunit.InlineData("mode", "EquivalentToMain")]
    [Xunit.InlineData("symlink", "EquivalentToMain")]
    [Xunit.InlineData("different", "AbsentFromMain")]
    [Xunit.InlineData("missing-main", "AbsentFromMain")]
    [Xunit.InlineData("missing-tip", "AbsentFromMain")]
    [Xunit.InlineData("gitlink", "Inconclusive")]
    public void Classify_ChangedPathComparison_ReturnsOnlyProvenState(string comparison, string expected)
    {
        // A path is preserved verbatim, including whitespace, quotes and newlines.
        const string path = " leading space/quote\"\tline\nend ";
        var changed = Record("100644", "100644", OldBlob, NewBlob, "M", path);
        var difference = comparison switch
        {
            "same" => Record("100644", "100644", NewBlob, NewBlob, "M", path),
            "mode" => Record("100644", "100755", NewBlob, NewBlob, "M", path),
            "symlink" => Record("100644", "120000", NewBlob, NewBlob, "T", path),
            "different" => Record("100644", "100644", OldBlob, NewBlob, "M", path),
            "missing-main" => Record("000000", "100644", Missing, NewBlob, "A", path),
            "missing-tip" => Record("100644", "000000", OldBlob, Missing, "D", path),
            "gitlink" => Record("160000", "100644", OldBlob, NewBlob, "T", path),
            _ => ""
        };
        // Differences on paths outside the branch's delta do not affect equality.
        difference += Record("100644", "100644", OldBlob, NewBlob, "M", "unrelated");

        Assert.Equal(expected, Classify(changed, difference).ToString());
    }

    [Xunit.Fact]
    public void Classify_DeletionAbsentAtBothTips_IsEquivalent()
    {
        var changed = Record("100644", "000000", OldBlob, Missing, "D", "deleted");
        Assert.Equal(GoalBranchContentState.EquivalentToMain, Classify(changed, ""));
    }

    [Xunit.Fact]
    public void Classify_RenameOldPathStillOnMain_IsAbsent()
    {
        var deletion = Record("100644", "000000", OldBlob, Missing, "D", "old");
        var addition = Record("000000", "100644", Missing, OldBlob, "A", "new");
        Assert.Equal(GoalBranchContentState.AbsentFromMain, Classify(deletion + addition, deletion));
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, "exit")]
    [Xunit.InlineData(1, "exit")]
    [Xunit.InlineData(2, "exit")]
    [Xunit.InlineData(0, "drain")]
    [Xunit.InlineData(1, "drain")]
    [Xunit.InlineData(2, "drain")]
    [Xunit.InlineData(0, "start")]
    [Xunit.InlineData(1, "start")]
    [Xunit.InlineData(2, "start")]
    public void Classify_IncompleteGitAtAnyPhase_IsInconclusive(int phase, string fault)
    {
        var calls = 0;
        var output = new[] { Base + "\n", Record("100644", "100644", OldBlob, NewBlob, "M", "path"), "" };
        var result = GoalBranchTreeContentDetector.Classify(Path.GetTempPath(), Main, "goal/pinned", Tip, (_, _) =>
        {
            var current = calls++;
            return current == phase
                ? new GitCli.GitResult(fault == "exit" ? 1 : 0, output[current], "injected failure",
                    DrainTimedOut: fault == "drain", ProcessStarted: fault != "start")
                : new GitCli.GitResult(0, output[current], "");
        });
        Assert.Equal(GoalBranchContentState.Inconclusive, result);
        Assert.Equal(phase + 1, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("not-a-sha")]
    [Xunit.InlineData("1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\n")]
    public void Classify_UnresolvedOrAmbiguousBase_IsInconclusive(string output)
    {
        var calls = 0;
        var result = GoalBranchTreeContentDetector.Classify(Path.GetTempPath(), Main, "goal/pinned", Tip, (_, args) =>
        {
            calls++;
            Assert.Equal("merge-base", args[0]);
            return new GitCli.GitResult(0, output, "");
        });
        Assert.Equal(GoalBranchContentState.Inconclusive, result);
        Assert.Equal(1, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("tip")]
    public void Classify_UnresolvedTip_DoesNotRunGit(string? tip)
    {
        Assert.Equal(GoalBranchContentState.Inconclusive,
            GoalBranchTreeContentDetector.Classify(Path.GetTempPath(), Main, "goal/pinned", tip,
                (_, _) => throw new Xunit.Sdk.XunitException("An immutable tip is required")));
    }

    [Xunit.Theory]
    [Xunit.InlineData("empty")]
    [Xunit.InlineData("truncated")]
    [Xunit.InlineData("header")]
    [Xunit.InlineData("path")]
    [Xunit.InlineData("duplicate")]
    [Xunit.InlineData("mode")]
    [Xunit.InlineData("gitlink")]
    [Xunit.InlineData("replacement-character")]
    [Xunit.InlineData("invalid-absence")]
    [Xunit.InlineData("rename-record")]
    public void Classify_UnsupportedChangedSet_IsInconclusive(string kind)
    {
        var valid = Record("100644", "100644", OldBlob, NewBlob, "M", "path");
        var changed = kind switch
        {
            "empty" => "",
            "truncated" => valid[..^1],
            "header" => ":invalid\0path\0",
            "path" => Record("100644", "100644", OldBlob, NewBlob, "M", ""),
            "duplicate" => valid + valid,
            "mode" => Record("040000", "100644", OldBlob, NewBlob, "T", "path"),
            "gitlink" => Record("160000", "160000", OldBlob, NewBlob, "M", "path"),
            "replacement-character" => valid.Replace("path", "bad\ufffdname"),
            "invalid-absence" => Record("000000", "100644", OldBlob, NewBlob, "A", "path"),
            "rename-record" => Record("100644", "100644", OldBlob, NewBlob, "R100", "old") + "new\0",
            _ => throw new ArgumentException(kind)
        };
        Assert.Equal(GoalBranchContentState.Inconclusive, Classify(changed, "", expectedCalls: 2));
    }

    [Xunit.Fact]
    public void Classify_MalformedComparison_IsInconclusive()
    {
        Assert.Equal(GoalBranchContentState.Inconclusive,
            Classify(Record("100644", "100644", OldBlob, NewBlob, "M", "path"), "truncated"));
    }

    private static GoalBranchContentState Classify(string changed, string difference, int expectedCalls = 3)
    {
        var calls = 0;
        var root = Path.GetTempPath();
        var result = GoalBranchTreeContentDetector.Classify(root, Main, "goal/moving-name", Tip, (directory, args) =>
        {
            Assert.Equal(root, directory);
            var current = calls++;
            if (current == 0)
            {
                Assert.Equal(new[] { "merge-base", "--all", Main, Tip }, args.ToArray());
                return new GitCli.GitResult(0, Base + "\n", "");
            }
            Assert.Equal(new[] { "diff-tree", "-r", "-z", "--no-renames", "--no-abbrev",
                "--ignore-submodules=none", "--no-relative", current == 1 ? Base : Main, Tip }, args.ToArray());
            return new GitCli.GitResult(0, current == 1 ? changed : difference, "");
        });
        Assert.Equal(expectedCalls, calls);
        return result;
    }

    private static string Record(string oldMode, string newMode, string oldId, string newId, string status, string path) =>
        $":{oldMode} {newMode} {oldId} {newId} {status}\0{path}\0";
}
