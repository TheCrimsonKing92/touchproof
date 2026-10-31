using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scripted processes, no filesystem or ambient runner mutations.
public sealed class GitHubPullRequestStatusReaderTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Url = "https://github.com/octo/sample/pull/7";

    [Theory]
    [InlineData("OPEN", null, null, null, PullRequestStatusKind.Open)]
    [InlineData("MERGED", Commit, "2026-10-31T12:00:00Z", "2026-10-31T12:00:00Z", PullRequestStatusKind.Merged)]
    [InlineData("CLOSED", null, null, "2026-10-31T12:00:00Z", PullRequestStatusKind.ClosedUnmerged)]
    [InlineData("MERGED", null, "2026-10-31T12:00:00Z", null, PullRequestStatusKind.Unavailable)]
    [InlineData("MERGED", Commit, null, null, PullRequestStatusKind.Unavailable)]
    [InlineData("OPEN", null, "2026-10-31T12:00:00Z", null, PullRequestStatusKind.Unavailable)]
    [InlineData("OPEN", null, null, "2026-10-31T12:00:00Z", PullRequestStatusKind.Unavailable)]
    [InlineData("CLOSED", Commit, null, "2026-10-31T12:00:00Z", PullRequestStatusKind.Unavailable)]
    [InlineData("FUTURE", null, null, null, PullRequestStatusKind.Unavailable)]
    public void Classifies_only_consistent_positive_evidence(string state, string? commit, string? mergedAt,
        string? closedAt, PullRequestStatusKind expected)
    {
        var runner = new Runner { Response = new(true, 0, JsonSerializer.Serialize(new
        { state, mergedAt, mergeCommit = commit is null ? null : new { oid = commit }, closedAt, url = Url }), "") };
        var result = new GitHubPullRequestStatusReader(runner).Read(Path.GetTempPath(), 7);
        Assert.Equal(expected, result.Kind);
        if (expected == PullRequestStatusKind.Merged) Assert.Equal(Commit, result.MergeCommit);
        if (expected == PullRequestStatusKind.Unavailable) Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(["pr", "view", "7", "--repo", "octo/sample", "--json", "state,mergedAt,mergeCommit,closedAt,url"], runner.GhArguments);
    }

    [Theory]
    [InlineData(1, false, "{}")]
    [InlineData(4, false, "{}")]
    [InlineData(0, true, "{}")]
    [InlineData(0, false, "not json")]
    [InlineData(0, false, "")]
    [InlineData(0, false, "{\"state\":\"OPEN\"}")]
    [InlineData(0, false, "{\"state\":\"CLOSED\",\"mergedAt\":null,\"mergeCommit\":null,\"closedAt\":null,\"url\":null}")]
    public void Faults_are_unavailable(int exit, bool timedOut, string json)
    {
        var runner = new Runner { Response = new(true, exit, json, "fault", timedOut) };
        var status = new GitHubPullRequestStatusReader(runner).Read(Path.GetTempPath(), 7);
        Assert.Equal(PullRequestStatusKind.Unavailable, status.Kind);
        Assert.False(string.IsNullOrWhiteSpace(status.Reason));
    }

    [Fact]
    public void Unsupported_origin_does_not_invoke_gh()
    {
        var runner = new Runner { Origin = "https://gitlab.com/octo/sample.git" };
        Assert.Equal(PullRequestStatusKind.Unavailable, new GitHubPullRequestStatusReader(runner).Read(Path.GetTempPath(), 7).Kind);
        Assert.Null(runner.GhArguments);
    }

    [Fact]
    public void Cancellation_propagates()
    {
        var runner = new Runner { Cancel = true };
        Assert.Throws<OperationCanceledException>(() => new GitHubPullRequestStatusReader(runner).Read(Path.GetTempPath(), 7));
    }

    private sealed class Runner : IForgeProcessRunner
    {
        internal string Origin = "git@github.com:octo/sample.git";
        internal ForgeProcessResult Response = new(true, 0, "{}", "");
        internal string[]? GhArguments;
        internal bool Cancel;
        public ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            if (Cancel) throw new OperationCanceledException();
            Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
            if (fileName == "git") { Assert.Equal(["remote", "get-url", "origin"], arguments); return new(true, 0, Origin, ""); }
            Assert.Equal("gh", fileName);
            GhArguments = arguments.ToArray();
            return Response;
        }
    }
}
