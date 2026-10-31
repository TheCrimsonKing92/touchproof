using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Publishes through origin and gh's own login; never obtains or retains credentials.</summary>
public sealed class GitHubPullRequestPublisher(IForgeProcessRunner runner) : IPullRequestPublisher
{
    public PullRequestPublishResult Publish(PullRequestPublishRequest request)
    {
        string? pushed = null;
        var failureKind = PullRequestPublishFailureKind.NoGitHubOrigin;
        PullRequestPublishResult Failure(PullRequestPublishFailureKind kind, string message) =>
            new(null, null, pushed, kind, ForgeDiagnosticRedactor.Redact(message), pushed is null ? null : request.GoalBranch);
        try
        {
            var root = Path.GetFullPath(request.RepositoryRoot);
            var environment = new Dictionary<string, string>
            {
                ["GIT_CEILING_DIRECTORIES"] = Directory.GetParent(root)?.FullName ?? root,
                ["GIT_TERMINAL_PROMPT"] = "0"
            };
            ForgeProcessResult Run(string command, params string[] args) =>
                runner.Run(command, args, root, environment, TimeSpan.FromSeconds(command == "git" ? 60 : 30));

            var origin = Run("git", "remote", "get-url", "origin");
            if (!Ok(origin)) return Failure(failureKind, Diagnostic(origin));
            if (!GitHubRemote.TryParse(origin.StandardOutput.Trim(), out var repository))
                return Failure(failureKind, "origin is not a supported GitHub repository URL");

            failureKind = PullRequestPublishFailureKind.PushRejected;
            if (!Regex.IsMatch(request.GoalBranch, @"\Agoal/[0-9a-f]{8}\z") ||
                !IsRevision(request.CandidateCommit) ||
                (request.PreviouslyPushedCommit is { } previous && !IsRevision(previous)))
                return Failure(failureKind, "publication requires a goal branch and full Git revisions");
            var reference = "refs/heads/" + request.GoalBranch;
            var remote = Run("git", "ls-remote", "--refs", "origin", reference);
            if (!Ok(remote)) return Failure(failureKind, Diagnostic(remote));
            var rows = remote.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string? remoteTip = null;
            if (rows.Length > 0)
            {
                var columns = rows[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (rows.Length != 1 || columns.Length != 2 || columns[1] != reference || !IsRevision(columns[0]))
                    return Failure(failureKind, "unreadable remote goal branch");
                remoteTip = columns[0];
            }
            if (!string.Equals(remoteTip, request.CandidateCommit, StringComparison.OrdinalIgnoreCase))
            {
                // Observing the remote tip does not grant authority to replace it.
                if (remoteTip is not null && !string.Equals(remoteTip, request.PreviouslyPushedCommit, StringComparison.OrdinalIgnoreCase))
                    return Failure(failureKind, "remote goal branch is not owned by this publication");
                var push = Run("git", "push", $"--force-with-lease={reference}:{request.PreviouslyPushedCommit ?? ""}",
                    "origin", $"{request.CandidateCommit}:{reference}");
                if (!Ok(push)) return Failure(failureKind, Diagnostic(push));
            }
            pushed = request.CandidateCommit;
            failureKind = PullRequestPublishFailureKind.GhFailed;
            var lookup = Run("gh", "pr", "list", "--repo", repository!, "--head", request.GoalBranch,
                "--base", request.Trunk, "--state", "open", "--json", "number,url");
            if (!Ok(lookup)) return Failure(GhFailure(lookup), Diagnostic(lookup));
            using var document = JsonDocument.Parse(lookup.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Failure(failureKind, "unreadable gh pr list response");
            int? number = null;
            string? url = null;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("number", out var n) ||
                    !n.TryGetInt32(out var candidateNumber) || !row.TryGetProperty("url", out var u) ||
                    u.ValueKind != JsonValueKind.String || !TryPullRequest(u.GetString(), repository!, out var urlNumber) || urlNumber != candidateNumber)
                    return Failure(failureKind, "unreadable gh pr list entry");
                if (number is null || candidateNumber < number) { number = candidateNumber; url = u.GetString(); }
            }
            if (number is not null) return new(number, url, pushed);

            var temporaryDirectory = Path.GetFullPath(Path.GetTempPath());
            if (Within(temporaryDirectory, root))
                return Failure(failureKind, "temporary PR body directory must be outside the repository");
            var bodyPath = Path.Combine(temporaryDirectory, $"mcg-pr-{Guid.NewGuid():N}.md");
            try
            {
                File.WriteAllText(bodyPath, request.Body);
                var create = Run("gh", "pr", "create", "--repo", repository!, "--base", request.Trunk,
                    "--head", request.GoalBranch, "--title", request.Title, "--body-file", bodyPath);
                if (!Ok(create)) return Failure(GhFailure(create), Diagnostic(create));
                url = create.StandardOutput.Trim();
                return TryPullRequest(url, repository!, out var createdNumber)
                    ? new(createdNumber, url, pushed)
                    : Failure(failureKind, "unreadable gh pr create response");
            }
            finally { if (File.Exists(bodyPath)) File.Delete(bodyPath); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(failureKind, exception is JsonException ? "unreadable gh response" : exception.Message);
        }
    }

    private static bool Ok(ForgeProcessResult result) => result.Started && !result.TimedOut && result.ExitCode == 0;
    private static bool IsRevision(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    private static string Diagnostic(ForgeProcessResult result) =>
        !result.Started ? $"forge command did not start: {result.StandardError}" : result.TimedOut ? "forge command timed out" :
        string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
    private static PullRequestPublishFailureKind GhFailure(ForgeProcessResult result) =>
        result.ExitCode == 4 || Regex.IsMatch(result.StandardError + result.StandardOutput, @"(?i)gh auth login|not logged|not signed in|HTTP 401")
            ? PullRequestPublishFailureKind.GhNotSignedIn : PullRequestPublishFailureKind.GhFailed;
    private static bool TryPullRequest(string? value, string repository, out int number)
    {
        number = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        var prefix = $"/{repository}/pull/";
        return uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(uri.AbsolutePath[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }
    private static bool Within(string path, string root) =>
        Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
